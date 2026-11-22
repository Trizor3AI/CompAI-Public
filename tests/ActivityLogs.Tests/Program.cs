using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) => { var path = Path.Combine(libraryPath, name.Name + ".dll"); return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null; };
if (args is ["--diagnostics-only"])
{
    ChallengeDiagnosticChecks.Run();
    ActivityDiagnosticChecks.Run();
    GoldSaucerChildDiagnosticChecks.Run();
    return;
}
Checks.Run();
ChallengeDiagnosticChecks.Run();
ActivityDiagnosticChecks.Run();
GoldSaucerChildDiagnosticChecks.Run();
ChallengeVisibleCounterChecks.Run();
if (args is ["--produce-fixtures", var output]) Checks.Produce(output);

internal static class Checks
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
    private static int passed;
    internal static void Run()
    {
        Test("installed activity log bounds match typed shapes", () =>
        {
            var player = new PlayerState(); var note = new ContentsNote(); var hunting = new MonsterNoteManager();
            Check(player.CompletedAdventuresBitArray.BitCount == 340 && player.CompletedAdventures.Length == 43);
            Check(note.CompletionFlagsBitArray.BitCount == 104 && note.CompletionFlags.Length == 13 && note.DisplayIds.Length == 19 && note.DisplayStatuses.Length == 19);
            Check(hunting.RankData.Length == 12 && hunting.RankData[0].RankData.Length == 10 && hunting.RankData[0].RankData[0].Counts.Length == 4);
        });
        Test("sightseeing passes native indexes zero through339 not global or displayed IDs", () =>
        {
            var calls = new List<uint>();
            var result = SightseeingLogReader.Describe(Time, [new(0x210000, "First"), new(0x210153, "Last")], index => { calls.Add(index); return index == 0; });
            Check(calls.SequenceEqual([0u,339u]) && result.Entries[0].Completed == true && result.Entries[1].Completed is null);
            Check(result.Entries[1].State == "not_observed_complete");
            foreach (var id in new uint[] { 0, 0x20FFFF, 0x210154, uint.MaxValue })
                Check(!SightseeingLogReader.Describe(Time, [new(id, "Invalid")], _ => throw new Exception("Invalid native call")).Available);
        });
        Test("sightseeing unavailable and invalid catalogs never call native methods", () =>
        {
            Check(!SightseeingLogReader.Describe(Time, Forbidden<SightseeingDefinition>(), _ => throw new Exception(), false).Available);
            Check(!SightseeingLogReader.Describe(Time, [new(0x210000, "One"), new(0x210000, "Duplicate")], _ => throw new Exception()).Available);
            Check(!SightseeingLogReader.Describe(Time, Enumerable.Range(0,341).Select(i=>new SightseeingDefinition((uint)(0x210000+i), "Future")), _ => throw new Exception()).Available);
        });
        Test("book mapping keeps classes rogue arcanist and Grand Companies distinct", () =>
        {
            Check(HuntingLogReader.BookIndex(10000,1)==0 && HuntingLogReader.BookIndex(10000,7)==6);
            Check(HuntingLogReader.BookIndex(10000,26)==7 && HuntingLogReader.BookIndex(10000,29)==11);
            Check(HuntingLogReader.BookIndex(1000000,1)==8 && HuntingLogReader.BookIndex(1000000,3)==10);
            Check(HuntingLogReader.BookIndex(10000,8)==-1 && HuntingLogReader.BookIndex(1,1)==-1);
            var agent = new AgentMonsterNote { BaseId = 1000000, ClassId = 3, Rank = 2 };
            Check(agent.GetMonsterNoteIdForIndex(0)==3000021 && agent.GetMonsterNoteIdForIndex(9)==3000030);
        });
        Test("current hunting counters sum validated individual targets", () =>
        {
            var observation = Hunt(); observation.Counts[0]=3; observation.Counts[1]=2; observation.Counts[4]=1;
            var snapshot = HuntingLogReader.Describe(Time,HuntDefinitions(observation),observation);
            Check(snapshot.Entries.Count==10 && snapshot.Entries[0].Completed==true && snapshot.Entries[0].Progress==5);
            Check(snapshot.Entries[1].Progress==1 && snapshot.Entries[1].Required==5 && snapshot.Entries[1].Completed is null);
            Check(snapshot.Entries[2].Progress==0 && snapshot.Entries[2].Rank==1);
        });
        Test("prior hunting ranks report completed requirements and future ranks stay unknown", () =>
        {
            var before=Hunt(progress:1); var complete=HuntingLogReader.Describe(Time,HuntDefinitions(before),before);
            Check(complete.Entries.All(row=>row.Completed==true && row.Progress==5 && row.Required==5));
            var after=Hunt(selected:1,progress:0); var future=HuntingLogReader.Describe(Time,HuntDefinitions(after),after);
            Check(future.Entries.All(row=>row.State=="unknown" && row.Completed is null && row.Progress is null));
        });
        Test("completed final class and company ranks ignore reset counters and preserve task totals", () =>
        {
            // Class rank 5 and Grand Company rank 3 reproduce the completed-book
            // shape, including a multi-target task. Counter bytes no longer belong
            // to these completed pages, so even unrelated values must not leak in.
            foreach(var (baseId, classId, selected, advanced, book) in new (uint,byte,byte,int,int)[]
                { (10000,2,4,5,1), (1000000,2,2,3,9) })
            {
                var observation=new HuntingObservation(1,2,baseId,classId,0,selected,0,book,advanced,
                    Enumerable.Range(0,10).Select(i=>baseId*classId+(uint)(selected*10+i+1)).ToArray(),
                    Enumerable.Repeat((byte)255,40).ToArray());
                var definitions=observation.Ids.Select((id,index)=>new HuntingDefinition(id,"Synthetic completed task "+id,
                    index==4 && baseId==10000 ? [3,3,3,0] : [3,0,0,0])).ToArray();
                var snapshot=HuntingLogReader.Describe(Time,definitions,observation);
                Check(snapshot.Available && snapshot.Entries.All(row=>row.Completed==true && row.Progress==row.Required));
                var result=new ProgressionLookup(snapshot).Query(Args("{\"category\":\"hunting_log\"}"));
                Check(result.entries[0].progress==3 && result.entries[0].required==3);
                Check(result.entries[4].progress==(baseId==10000 ? 9 : 3));
                Check(snapshot.MenuPage==book*5+selected);
            }
        });
        Test("hunting wrong identities unsupported counts and hidden state fail closed", () =>
        {
            var observation=Hunt(); observation.Counts[2]=1;
            Check(HuntingLogReader.Describe(Time,HuntDefinitions(observation),observation).Entries[0].State=="unknown");
            Check(!HuntingLogReader.Describe(Time,Forbidden<HuntingDefinition>(),observation,false).Available);
            var bad=Hunt() with { BookIndex=8 };
            Check(!HuntingLogReader.Describe(Time,HuntDefinitions(bad),bad).Available);
            var wrong=Hunt(); wrong.Ids[0]=10002;
            Check(!HuntingLogReader.Describe(Time,[],wrong).Available);
            var beyond=Hunt(progress:5);
            Check(!HuntingLogReader.Describe(Time,HuntDefinitions(beyond),beyond).Available);
        });
        Test("hunting capture stability includes selected book rank filter counters and native identity", () =>
        {
            var first=Hunt(); var copy=Hunt(); Check(first.SameAs(copy));
            Check(!first.SameAs(copy with { Manager=99 })); Check(!first.SameAs(copy with { Filter=1 }));
            Check(!first.SameAs(copy with { SelectedRank=1 })); copy.Counts[0]=1; Check(!first.SameAs(copy));
        });
        Test("challenge completion and corroborated counters are separate evidence", () =>
        {
            var result=Challenge();
            Check(result.Entries[0].Completed==true && result.Entries[0].Progress==3 && result.Entries[0].Required==3);
            Check(result.Entries.Single(row=>row.Id==47).Progress==1 && result.Entries.Single(row=>row.Id==47).Required==3);
            Check(result.Entries.Single(row=>row.Id==56).Progress==0 && result.Entries.Single(row=>row.Id==56).Completed is null);
            Check(result.Entries.Single(row=>row.Id==67).Progress==5 && result.Entries.Single(row=>row.Id==67).Completed==true);
        });
        Test("ordinary challenge targets are known without guessing partial counters", () =>
        {
            // Row 11 is Just Leve It to Me (the reported 3/5 case). Its raw status
            // is not assumed to encode a count merely because it fits the target.
            foreach(var id in new uint[]{2,11})
            foreach(var rawStatus in new[]{-1,0,2,3,5,int.MaxValue})
            {
                var snapshot=ChallengeLogReader.Describe(Time,[new(id,"Synthetic ordinary objective",5)],[new(id,rawStatus,false)]);
                var row=snapshot.Entries.Single();
                Check(row.State=="not_observed_complete" && row.Completed is null && row.Progress is null && row.Required==5);
                Check(row.UnknownReason!.Contains(id==11 ? "could not be verified together" : "Partial progress is not supported"));
                var result=new ProgressionLookup(snapshot).Query(Args("{\"category\":\"challenge_log\"}"));
                Check(result.entries.Single().progress is null && result.entries.Single().required==5);
                var completed=ChallengeLogReader.Describe(Time,[new(id,"Synthetic ordinary objective",5)],[new(id,rawStatus,true)]);
                Check(completed.Entries.Single().Completed==true && completed.Entries.Single().Progress==5);
            }
        });
        Test("challenge reset or counter disagreement becomes unknown not stale completion", () =>
        {
            foreach(var row in new ChallengeRowObservation[] {new(47,4,true),new(47,-1,false),new(47,3,false),new(47,0,true)})
            {
                var result=ChallengeLogReader.Describe(Time,ChallengeDefinitions(),[row]);
                Check(result.Entries.Single().State=="unknown" && result.Entries.Single().Progress is null && result.Entries.Single().Completed is null);
            }
            Check(!ChallengeLogReader.Describe(Time,Forbidden<ChallengeDefinition>(),Forbidden<ChallengeRowObservation>(),false).Available);
            Check(!ChallengeLogReader.Describe(Time,ChallengeDefinitions(),[new(47,0,false),new(47,0,false)]).Available);
            Check(!ChallengeLogReader.Describe(Time,ChallengeDefinitions(),[new(104,0,false)]).Available);
        });
        Test("challenge stability includes flags displayed IDs tab and statuses", () =>
        {
            var first=new ChallengeObservation(1,2,10,"00",[new(47,1,false)]);
            Check(first.SameAs(new(1,2,10,"00",[new(47,1,false)])));
            Check(!first.SameAs(first with { Tab=11 })); Check(!first.SameAs(first with { Bitmap="01" }));
            Check(!first.SameAs(first with { Rows=[new(47,0,false)] }));
        });
        Test("log snapshot rules reject false completion invented counters and saved menu metadata", () =>
        {
            foreach(var category in new[]{"hunting_log","sightseeing_log","challenge_log"})
                Throws(()=>new ProgressionSnapshot(category,Time,true,ProgressionSnapshot.ReadyState(category),[new(1,"Synthetic","not_completed",false)]));
            Throws(()=>new ProgressionSnapshot("challenge_log",Time,true,"visible_menu_observation",[new(1,"Synthetic","not_observed_complete",Progress:2,Required:3,UnknownReason:"No supported counter")]));
            Throws(()=>new ProgressionSnapshot("challenge_log",Time,true,"visible_menu_observation",[new(1,"Synthetic","completed",true,Progress:2,Required:3)]));
            foreach(var required in new long[]{0,-1,(long)int.MaxValue+1})
                Throws(()=>new ProgressionSnapshot("challenge_log",Time,true,"visible_menu_observation",[new(1,"Synthetic","not_observed_complete",Required:required,UnknownReason:"No supported counter")]));
            Throws(()=>new ProgressionSnapshot("hunting_log",Time,true,"visible_menu_observation",[new(1,"Synthetic","completed",true,Rank:1,Progress:0,Required:3)]));
            Throws(()=>new ProgressionSnapshot("challenge_log",Time,true,"saved_menu_observations",[new(47,"Synthetic","completed",true)]));
        });
        Test("public catalog identities invalidate on sheet game data and language changes", () =>
        {
            var sheet=new object();var second=new object();var names=new object();var game=new object();
            var sight=new SightseeingCatalog(sheet,game,"English",[]);
            Check(sight.Matches(sheet,game,"English")&&!sight.Matches(sheet,game,"Japanese")&&!sight.Matches(new object(),game,"English"));
            var challenge=new ChallengeCatalog(sheet,second,game,"English",[]);
            Check(challenge.Matches(sheet,second,game,"English")&&!challenge.Matches(sheet,new object(),game,"English"));
            var hunt=new HuntingCatalog(sheet,second,names,game,"English",[]);
            Check(hunt.Matches(sheet,second,names,game,"English")&&!hunt.Matches(sheet,second,new object(),game,"English"));
        });
        Console.WriteLine($"Passed {passed} activity log checks.");
    }

    private static HuntingObservation Hunt(byte selected=0,int progress=0) => new(1,2,10000,1,0,selected,0,0,progress,
        Enumerable.Range(0,10).Select(i=>(uint)(10001+selected*10+i)).ToArray(),new byte[40]);
    private static HuntingDefinition[] HuntDefinitions(HuntingObservation value) => value.Ids.Select(id=>new HuntingDefinition(id,"Synthetic hunting task "+id,[3,2,0,0])).ToArray();
    private static ChallengeDefinition[] ChallengeDefinitions()=>[new(1,"Synthetic battle challenge",3),new(2,"Synthetic ordinary challenge",5),
        new(11,"Synthetic leve I",5,"Just Leve It to Me"),new(12,"Synthetic leve II",20,"Making Like a Tree"),
        new(47,"Synthetic Verminion",3),new(56,"Synthetic Eureka",30),new(67,"Synthetic Verminion III",5)];
    private static ProgressionSnapshot Challenge()
    {
        var definitions=ChallengeDefinitions();
        ChallengeRowObservation[] rows=[new(1,-1,true),new(2,2,false),new(11,3,false),new(12,20,true),new(47,1,false),new(56,0,false),new(67,5,true)];
        var counters=ChallengeLogReader.VerifyVisibleCounters(4,definitions,rows,ChallengeVisibleCounterChecks.Texts());
        return ChallengeLogReader.Describe(Time,definitions,rows,menuPage:4,visibleCounters:counters);
    }
    internal static void Produce(string output)
    {
        var hunt=Hunt();hunt.Counts[0]=3;hunt.Counts[1]=2;
        var snapshots=new[]{HuntingLogReader.Describe(Time,HuntDefinitions(hunt),hunt),
            SightseeingLogReader.Describe(Time,[new(0x210000,"Synthetic vista first"),new(0x210153,"Synthetic vista last")],index=>index==0),Challenge()};
        var fixtures=new List<object>();
        foreach(var snapshot in snapshots)
        {
            foreach(var tail in new[]{"",",\"state\":\"completed\"",",\"state\":\"not_observed_complete\"",",\"limit\":1",",\"offset\":1,\"limit\":1"})
            {
                var args=Args("{\"category\":\""+snapshot.Category+"\""+tail+"}");
                fixtures.Add(new{name=snapshot.Category+tail,arguments=args,result=new ProgressionLookup(snapshot).Query(args)});
            }
            var arguments=Args("{\"category\":\""+snapshot.Category+"\"}");
            fixtures.Add(new{name=snapshot.Category+" unavailable",arguments,result=new ProgressionLookup(ProgressionSnapshot.Unavailable(snapshot.Category,"Synthetic unloaded/hidden source")).Query(arguments)});
        }
        var full=Path.GetFullPath(output);Directory.CreateDirectory(Path.GetDirectoryName(full)!);File.WriteAllText(full,JsonSerializer.Serialize(fixtures,new JsonSerializerOptions{WriteIndented=true}));
    }
    private static IEnumerable<T> Forbidden<T>(){yield return Fail<T>();}
    private static T Fail<T>()=>throw new Exception("Unavailable source enumerated");
    private static JsonElement Args(string text)=>JsonDocument.Parse(text).RootElement.Clone();
    private static void Check(bool value){if(!value)throw new Exception("Assertion failed");}
    private static void Test(string name,Action action){action();passed++;Console.WriteLine("PASS "+name);}
    private static void Throws(Action action){try{action();}catch(ArgumentException){return;}throw new Exception("Expected invalid snapshot");}
}
