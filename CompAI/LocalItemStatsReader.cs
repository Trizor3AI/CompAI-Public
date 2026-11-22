using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Detached public sheet values only; called on the framework thread.</summary>
internal sealed class LocalItemStatsReader(IDataManager data)
{
    private (object Sheet, object GameData, string Language, Dictionary<uint, GameDefinitionMateriaStats> Rows)? materia;
    // Current meldable substats, including crafting and gathering. Existing attributes are also retained.
    private static readonly uint[] BattleMeldStats = [6, 19, 22, 27, 44, 45, 46];

    internal GameDefinitionItemStats Capture(Item row)
    {
        GameDefinitionEquipmentStats? equipment = null;
        string? equipmentReason = "No valid equipment slot category.";
        if (row.EquipSlotCategory.RowId > 0 && row.EquipSlotCategory.IsValid)
        {
            try { equipment = DescribeEquipment(row, data.GetExcelSheet<BaseParam>()); equipmentReason = null; }
            catch (Exception) { equipmentReason = "Local equipment stat fields unavailable or unsupported."; }
            return new(equipment, equipmentReason, null, "Materia lookup is only applied to non-equipment items.");
        }
        GameDefinitionMateriaStats? gem = null;
        string? materiaReason;
        try
        {
            var sheet = data.GetExcelSheet<Materia>();
            if (materia is not { } cached || !ReferenceEquals(cached.Sheet, sheet.RawSheet) ||
                !ReferenceEquals(cached.GameData, data.GameData) || cached.Language != data.Language.ToString())
                materia = (sheet.RawSheet, data.GameData, data.Language.ToString(), DescribeMateria(sheet));
            materia.Value.Rows.TryGetValue(row.RowId, out gem);
            materiaReason = gem is null ? "No supported unambiguous Materia stat definition." : null;
        }
        catch (Exception) { materia = null; materiaReason = "Local Materia definitions unavailable or unsupported."; }
        return new(equipment, equipmentReason, gem, materiaReason);
    }

    internal static Dictionary<uint, GameDefinitionMateriaStats> DescribeMateria(ExcelSheet<Materia> sheet)
    {
        if (sheet.Count > 4096) throw new InvalidOperationException("Materia sheet bound exceeded.");
        var result = new Dictionary<uint, GameDefinitionMateriaStats>();
        var seen = new HashSet<uint>();
        foreach (var row in sheet)
        {
            if (row.RowId == 0) continue;
            if (row.Item.Count != row.Value.Count || row.Item.Count > 16) throw new InvalidOperationException("Unsupported materia layout.");
            for (var index = 0; index < row.Item.Count; index++)
            {
                var item = row.Item[index];
                if (item.RowId == 0) continue;
                if (!seen.Add(item.RowId)) { result.Remove(item.RowId); continue; }
                // Deprecated materia can retain Item references with BaseParam=0/Value=0.
                // Keep those unknown without making unrelated current materia unavailable.
                if (!item.IsValid || !row.BaseParam.IsValid || row.BaseParam.RowId == 0 || row.Value[index] <= 0)
                    continue;
                var minimum = item.Value.LevelItem;
                result.Add(item.RowId, new(row.RowId, index, row.BaseParam.RowId,
                        GameDefinitionsReader.Name(row.BaseParam.Value.Name), row.Value[index],
                        minimum.RowId > 0 && minimum.IsValid ? minimum.RowId : null));
                if (result.Count > 4096) throw new InvalidOperationException("Materia index bound exceeded.");
            }
        }
        return result;
    }

    internal static Dictionary<uint, int> StatValues(IEnumerable<(uint Id, int Value)> source)
    {
        var result = new Dictionary<uint, int>();
        var slots = source.Take(17).ToArray();
        if (slots.Length > 16) throw new InvalidOperationException("Unsupported stat layout.");
        foreach (var (id, value) in slots)
        {
            if (id == 0 && value == 0) continue;
            if (id == 0 || value < 0 || value > short.MaxValue || !result.TryAdd(id, value))
                throw new InvalidOperationException("Unsupported stat slot.");
        }
        return result;
    }

    internal static GameDefinitionQualityValue QualityValue(int value, int bonus, bool canBeHq)
    {
        if (value is < 0 or > ushort.MaxValue || bonus is < 0 or > short.MaxValue || value + bonus > ushort.MaxValue)
            throw new InvalidOperationException("Unsupported quality stat.");
        return new(value, canBeHq ? value + bonus : null);
    }

    internal static GameDefinitionEquipmentStats DescribeEquipment(Item row, ExcelSheet<BaseParam> parameters)
    {
        if (row.BaseParam.Count != row.BaseParamValue.Count || row.BaseParamSpecial.Count != row.BaseParamValueSpecial.Count ||
            row.MateriaSlotCount > 5) throw new InvalidOperationException("Unsupported equipment layout.");
        var baseValues = StatValues(Enumerable.Range(0, row.BaseParam.Count).Select(i => (row.BaseParam[i].RowId, (int)row.BaseParamValue[i])));
        // BaseParamSpecial is an HQ increment on equipment; consumable effects are never interpreted here.
        var bonuses = StatValues(Enumerable.Range(0, row.BaseParamSpecial.Count).Select(i => (row.BaseParamSpecial[i].RowId, (int)row.BaseParamValueSpecial[i])));
        var ids = baseValues.Keys.Concat(bonuses.Keys).Where(id => id is not (12 or 13 or 21 or 24));
        if (row.MateriaSlotCount > 0)
        {
            uint[] meldStats = baseValues.Keys.Any(id => id is 70 or 71 or 11) ? [11, 70, 71] :
                baseValues.Keys.Any(id => id is 72 or 73 or 10) ? [10, 72, 73] : BattleMeldStats;
            ids = ids.Concat(meldStats);
        }
        var attributes = new List<GameDefinitionStat>();
        foreach (var id in ids.Distinct().OrderBy(id => id))
        {
            var values = QualityValue(baseValues.GetValueOrDefault(id), bonuses.GetValueOrDefault(id), row.CanBeHq);
            var valid = parameters.TryGetRow(id, out var param);
            int? cap = null;
            var reason = "Missing or unsupported cap factors.";
            if (valid && row.LevelItem.RowId > 0 && row.LevelItem.IsValid)
            {
                var level = LevelFactor(row.LevelItem.Value, id);
                var slot = SlotFactor(param, row.EquipSlotCategory.RowId);
                var role = row.BaseParamModifier < param.MeldParam.Count ? (int?)param.MeldParam[row.BaseParamModifier] : null;
                cap = CalculateCap(level, slot, role, Math.Max(values.base_value, values.hq_value ?? 0));
                if (cap is not null) reason = "";
            }
            attributes.Add(new(id, valid ? GameDefinitionsReader.Name(param.Name) : null, values.base_value, values.hq_value,
                cap, cap is null ? reason : null));
        }
        if (attributes.Count > 32) throw new InvalidOperationException("Equipment attribute bound exceeded.");
        return new(attributes.ToArray(), QualityValue(row.DamagePhys, bonuses.GetValueOrDefault(12u), row.CanBeHq),
            QualityValue(row.DamageMag, bonuses.GetValueOrDefault(13u), row.CanBeHq),
            QualityValue(row.DefensePhys, bonuses.GetValueOrDefault(21u), row.CanBeHq),
            QualityValue(row.DefenseMag, bonuses.GetValueOrDefault(24u), row.CanBeHq),
            row.Delayms, row.MateriaSlotCount, row.IsAdvancedMeldingPermitted);
    }

    // Current primary-source formula (with installed factors, not web data):
    // https://github.com/xiv-gear-planner/gear-planner/blob/main/packages/xivmath/src/xivmath.ts (statCapWithJob)
    // datamanager_new.ts calls this for item caps using BaseParam.MeldParam[Item.BaseParamModifier].
    // Apply role first and floor, apply slot and floor, then divide by ten and round half up.
    // All accepted inputs are positive integers: long division matches those floors without floating-point drift.
    // Offhand and compound slots deliberately remain unknown: their rounding/partition rules need separate verification.
    internal static int? CalculateCap(int? level, int? slot, int? role, int minimum)
    {
        if (level is not (> 0 and <= ushort.MaxValue) || slot is not (> 0 and <= 1000) || role is not (> 0 and <= 100) || minimum < 0)
            return null;
        var roleValue = (long)level.Value * role.Value / 100;
        var slotValue = roleValue * slot.Value / 100;
        var value = (int)((slotValue + 5) / 10);
        return value >= minimum && value <= ushort.MaxValue ? value : null;
    }

    private static int? SlotFactor(BaseParam p, uint slot) => slot switch
    {
        1 => p.OneHandWeaponPercent, 3 => p.HeadPercent, 4 => p.ChestPercent, 5 => p.HandsPercent,
        6 => p.WaistPercent, 7 => p.LegsPercent, 8 => p.FeetPercent, 9 => p.EarringPercent,
        10 => p.NecklacePercent, 11 => p.BraceletPercent, 12 => p.RingPercent, 13 => p.TwoHandWeaponPercent,
        _ => null,
    };

    private static int? LevelFactor(ItemLevel p, uint id) => id switch
    {
        1 => p.Strength, 2 => p.Dexterity, 3 => p.Vitality, 4 => p.Intelligence, 5 => p.Mind,
        6 => p.Piety, 7 => p.HP, 8 => p.MP, 10 => p.GP, 11 => p.CP, 19 => p.Tenacity,
        22 => p.DirectHitRate, 27 => p.CriticalHit, 44 => p.Determination, 45 => p.SkillSpeed, 46 => p.SpellSpeed,
        70 => p.Craftsmanship, 71 => p.Control, 72 => p.Gathering, 73 => p.Perception,
        _ => null,
    };
}
