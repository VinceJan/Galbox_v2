using System;
using System.IO;
using Galbox.Data.Entities;
using Galbox.App.Services;

// Simple test for engine detection
class Test
{
    static void Main()
    {
        // Test 1: Dreamin'_Her (Renpy)
        var game1 = new GameInfo
        {
            NameOriginal = "Dreamin'_Her",
            InstallPath = @"D:\GAME\Dreamin'_Her",
            MainExecutable = @"D:\GAME\Dreamin'_Her\dreaminher.exe"
        };
        var engine1 = EngineSaveDetector.DetectEngineType(game1);
        Console.WriteLine($"Game: {game1.NameOriginal}");
        Console.WriteLine($"InstallPath: {game1.InstallPath}");
        Console.WriteLine($"MainExecutable: {game1.MainExecutable}");
        Console.WriteLine($"Engine: {engine1}");
        Console.WriteLine();

        // Test 2: SabbatOfTheWitch (Krkr)
        var game2 = new GameInfo
        {
            NameOriginal = "SabbatOfTheWitch",
            InstallPath = @"D:\GAME\SabbatOfTheWitch",
            MainExecutable = @"D:\GAME\SabbatOfTheWitch\SabbatOfTheWitch.exe"
        };
        var engine2 = EngineSaveDetector.DetectEngineType(game2);
        Console.WriteLine($"Game: {game2.NameOriginal}");
        Console.WriteLine($"InstallPath: {game2.InstallPath}");
        Console.WriteLine($"MainExecutable: {game2.MainExecutable}");
        Console.WriteLine($"Engine: {engine2}");
    }
}
