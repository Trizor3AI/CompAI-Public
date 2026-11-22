using CompAI;

internal static class ChallengeVisibleCounterChecks
{
    private static readonly DateTimeOffset Time=DateTimeOffset.Parse("2026-10-23T12:00:00Z");
    private static ChallengeDefinition[] Definitions()=>[new(11,"Levequests — Just Leve It to Me",5,"Just Leve It to Me"),
        new(12,"Levequests — Making Like a Tree",20,"Making Like a Tree")];
    private static ChallengeRowObservation[] Rows()=>[new(11,3,false),new(12,20,true)];
    internal static ChallengeDiagnosticText[] Texts()=>[new(3,2,[8,5],"Just Leve It to Me"),new(11,7,[8,5],"3/5"),
        new(3,2,[8,51001],"Making Like a Tree"),new(11,7,[8,51001],"20/20")];
    internal static void Run()
    {
        var definitions=Definitions();var rows=Rows();var texts=Texts();
        var verified=ChallengeLogReader.VerifyVisibleCounters(4,definitions,rows,texts);
        Check(verified.Length==2 && verified[0].Progress==3 && verified[0].Required==5 && verified[1].Progress==20);
        var stable=ChallengeLogReader.StableVisibleCounters(123,verified,123,ChallengeLogReader.VerifyVisibleCounters(4,definitions,rows,texts.Reverse().ToArray()));
        var snapshot=ChallengeLogReader.Describe(Time,definitions,rows,menuPage:4,visibleCounters:stable);
        Check(snapshot.Entries[0].Progress==3 && snapshot.Entries[0].Required==5 && snapshot.Entries[0].Completed is null);
        Check(snapshot.Entries[1].Progress==20 && snapshot.Entries[1].Completed==true);
        Check(snapshot.Coverage.Length<=1000);
        var noUi=ChallengeLogReader.Describe(Time,definitions,rows,menuPage:4);
        Check(noUi.Entries[0].Progress is null && noUi.Entries[0].Required==5);
        Console.WriteLine("PASS observed leve title/ratio/native values produce 3/5 and20/20 only with visible corroboration");

        foreach(var badText in new[]{"2/5","3/6","3 /5","3/5/1","-1/5","3/05x","2147483648/5","３/5"})
        {
            var altered=Texts();altered[1]=altered[1] with { Text=badText };
            Check(ChallengeLogReader.VerifyVisibleCounters(4,definitions,rows,altered).All(value=>value.Id!=11));
        }
        foreach(var altered in new[]{
            texts.Append(texts[0]).ToArray(), texts.Append(texts[1]).ToArray(),
            texts.Select((value,index)=>index==0?value with{Text="Other objective"}:value).ToArray(),
            texts.Select((value,index)=>index==1?value with{ComponentPath=[8,51001]}:value).ToArray(),
            texts.Select((value,index)=>index==1?value with{NodeId=10}:value).ToArray(),
            texts.Select((value,index)=>index==1?value with{ParentNodeId=9}:value).ToArray()})
            Check(ChallengeLogReader.VerifyVisibleCounters(4,definitions,rows,altered).All(value=>value.Id!=11));
        Check(ChallengeLogReader.VerifyVisibleCounters(0,definitions,rows,texts).Length==0);
        Check(ChallengeLogReader.VerifyVisibleCounters(4,definitions,[new(11,4,false)],texts).Length==0);
        Check(ChallengeLogReader.VerifyVisibleCounters(4,definitions,[new(11,3,true)],texts).Length==0);
        Check(ChallengeLogReader.VerifyVisibleCounters(4,[new(13,"Other row",5,"Just Leve It to Me")],[new(13,3,false)],texts).Length==0);
        Console.WriteLine("PASS leve verifier rejects ambiguity, wrong row/tab/title/component and contradictory counters");

        Check(ChallengeLogReader.StableVisibleCounters(123,verified,124,verified).Length==0);
        Check(ChallengeLogReader.StableVisibleCounters(0,verified,0,verified).Length==0);
        Check(ChallengeLogReader.StableVisibleCounters(123,verified,123,[verified[0] with{Progress=4},verified[1]]).Length==0);
        Check(ChallengeLogReader.StableVisibleCounters(123,verified,123,[verified[0] with{ComponentId=51001},verified[1]]).Length==0);
        Check(ChallengeLogReader.StableVisibleCounters(123,verified,123,[]).Length==0);
        var localized=definitions.Select(value=>value.Id==11?value with{Title="Une mission localisée"}:value).ToArray();
        var localizedText=texts.Select((value,index)=>index==0?value with{Text="Une mission localisée"}:value).ToArray();
        Check(ChallengeLogReader.VerifyVisibleCounters(4,localized,rows,localizedText).Any(value=>value.Id==11));
        Console.WriteLine("PASS leve evidence requires stable addon/component/counters and uses exact localized sheet titles");
        ChallengeDefinition[] ventures = [new(33,"Retainers — Venture Capital",10,"Venture Capital"),
            new(34,"Retainers — Venture Bothers",15,"Venture Bothers")];
        ChallengeRowObservation[] ventureRows = [new(33,10,true),new(34,12,false)];
        ChallengeDiagnosticText[] ventureTexts = [new(3,2,[8,5],"Venture Capital"),new(11,7,[8,5],"10/10"),
            new(3,2,[8,51001],"Venture Bothers"),new(11,7,[8,51001],"12/15")];
        var ventureCounters=ChallengeLogReader.VerifyVisibleCounters(9,ventures,ventureRows,ventureTexts);
        Check(ventureCounters.Length==2);
        var ventureSnapshot=ChallengeLogReader.Describe(Time,ventures,ventureRows,menuPage:9,visibleCounters:ventureCounters);
        Check(ventureSnapshot.Entries.Single(row=>row.Id==34).Progress==12 && ventureSnapshot.Entries.Single(row=>row.Id==34).Required==15);
        Check(ChallengeLogReader.VerifyVisibleCounters(4,ventures,ventureRows,ventureTexts).Length==0);
        Check(ChallengeLogReader.VerifyVisibleCounters(9,definitions,rows,texts).Length==0);
        Check(ChallengeLogReader.VerifyVisibleCounters(9,ventures,[new(34,11,false)],ventureTexts).Length==0);
        Check(ChallengeLogReader.Describe(Time,ventures,ventureRows,menuPage:9).Entries.Single(row=>row.Id==34).Progress is null);
        Console.WriteLine("PASS retainer tab corroborates Venture Bothers12/15 without widening raw counter support");
    }
    private static void Check(bool value) { if(!value)throw new InvalidOperationException("Visible Challenge counter assertion failed."); }
}
