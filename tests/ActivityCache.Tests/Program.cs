using CompAI;
Plugin.RunActivityChecks();
Plugin.RunCosmicAccumulationChecks();
if (args.Length == 2 && args[0] == "--produce-island-fixtures") Plugin.ProduceIslandFixtures(args[1]);
if (args.Length == 2 && args[0] == "--produce-research-fixtures") Plugin.ProduceResearchFixtures(args[1]);
if (args.Length == 2 && args[0] == "--produce-resource-fixtures") Plugin.ProduceResourceFixtures(args[1]);
if (args.Length == 2 && args[0] == "--produce-fc-credit-fixtures") Plugin.ProduceFcCreditFixtures(args[1]);
if (args.Length == 2 && args[0] == "--produce-macro-fixtures") Plugin.ProduceMacroFixtures(args[1]);
