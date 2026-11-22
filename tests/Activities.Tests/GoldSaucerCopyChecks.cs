using System.Runtime.InteropServices;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game;

// Synthetic callbacks replace addresses only in this standalone fixture process. No game function runs.
internal static unsafe class GoldSaucerCopyChecks
{
    private static nint original;
    private static bool touchedOriginal;
    private static int starCalls;

    internal static void Run()
    {
        var addresses = new[] { RaceChocoboManager.Addresses.GetAttributesCurrent,
            RaceChocoboManager.Addresses.GetAttributesMaximum, RaceChocoboManager.Addresses.GetAttributesStars,
            RaceChocoboManager.Addresses.GetRating, RaceChocoboManager.Addresses.GetPedigreeLevel };
        var previous = addresses.Select(address => address.Value).ToArray();
        try
        {
            addresses[0].Value = (nint)(delegate* unmanaged<RaceChocoboManager*, RaceChocoboAttributeValues*, void>)&Current;
            addresses[1].Value = (nint)(delegate* unmanaged<RaceChocoboManager*, RaceChocoboAttributeValues*, void>)&Maximum;
            addresses[2].Value = (nint)(delegate* unmanaged<RaceChocoboManager*, RaceChocoboAttributeValues*, void>)&Stars;
            addresses[3].Value = (nint)(delegate* unmanaged<RaceChocoboManager*, int>)&Rating;
            addresses[4].Value = (nint)(delegate* unmanaged<RaceChocoboManager*, int>)&Pedigree;
            RaceChocoboManager native = new() { State = RaceChocoboManager.RaceChocoboState.Loaded,
                Rank = 17, NameFirst = 1, NameLast = 2, MaximumSpeed = 47, Acceleration = 41,
                Endurance = 39, Stamina = 44, Cunning = 35, Parameters = 0x75,
                FatherParameters = 0x4390, MotherParameters = 0x4363, Color = 37, SessionsAvailable = 8 };
            original = (nint)(&native);
            touchedOriginal = false;
            starCalls = 0;
            var copy = GoldSaucerReader.CopyRace(&native) ?? throw new Exception("Loaded synthetic race rejected.");
            Check(!touchedOriginal && starCalls == 3 && native.Parameters == 0x75);
            Check(native.FatherParameters == 0x4390 && native.MotherParameters == 0x4363);
            Check(copy.Current.MaximumSpeed == 141 && copy.Maximum.MaximumSpeed == 300);
            Check(copy.Stars == new GoldSaucerAttributes(4, 2, 2, 4, 4));
            Check(copy.FatherStars == new GoldSaucerAttributes(1, 2, 3, 4, 1));
            Check(copy.MotherStars == new GoldSaucerAttributes(4, 3, 2, 1, 4));
            Check(copy.Pedigree == 4 && copy.Rating == 67 && copy.Color == 37 && copy.SessionsAvailable == 8);
            native.MaximumSpeed = 101;
            Check(GoldSaucerReader.CopyRace(&native) is null && starCalls == 3);
        }
        finally
        {
            for (var index = 0; index < addresses.Length; ++index) addresses[index].Value = previous[index];
            original = 0;
        }
    }

    private static void Observe(RaceChocoboManager* copy) => touchedOriginal |= (nint)copy == original;
    [UnmanagedCallersOnly]
    private static void Current(RaceChocoboManager* copy, RaceChocoboAttributeValues* output)
    { Observe(copy); *output = new() { MaximumSpeed = 141, Acceleration = 90, Endurance = 85, Stamina = 132, Cunning = 77 }; }
    [UnmanagedCallersOnly]
    private static void Maximum(RaceChocoboManager* copy, RaceChocoboAttributeValues* output)
    { Observe(copy); *output = new() { MaximumSpeed = 300, Acceleration = 220, Endurance = 220, Stamina = 300, Cunning = 220 }; }
    [UnmanagedCallersOnly]
    private static void Stars(RaceChocoboManager* copy, RaceChocoboAttributeValues* output)
    {
        Observe(copy);
        ++starCalls;
        *output = (copy->Parameters & 0x1F) switch
        {
            0 => new() { MaximumSpeed = 0, Acceleration = 1, Endurance = 2, Stamina = 3, Cunning = 0 },
            31 => new() { MaximumSpeed = 3, Acceleration = 2, Endurance = 1, Stamina = 0, Cunning = 3 },
            21 => new() { MaximumSpeed = 3, Acceleration = 1, Endurance = 1, Stamina = 3, Cunning = 3 },
            _ => default,
        };
    }
    [UnmanagedCallersOnly] private static int Rating(RaceChocoboManager* copy) { Observe(copy); return 67; }
    [UnmanagedCallersOnly] private static int Pedigree(RaceChocoboManager* copy) { Observe(copy); return 4; }
    private static void Check(bool value) { if (!value) throw new Exception("Detached race-copy fixture failed."); }
}

