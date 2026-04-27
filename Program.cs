namespace WinGeminiWrapper;

internal static class Program
{
    private const string DefaultEvernoteDiagnosticNoteId = "c5592033-94e1-c16c-0416-72c4dc98b61b";

    [STAThread]
    private static void Main(string[] args)
    {
        if (TryRunEvernoteSingleNoteDiagnostic(args))
        {
            return;
        }

        ApplicationConfiguration.Initialize();

        using var loginForm = new LoginForm();
        if (loginForm.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        Application.Run(new MainForm());
    }

    private static bool TryRunEvernoteSingleNoteDiagnostic(string[] args)
    {
        if (args.Length == 0 ||
            !string.Equals(args[0], "--evernote-note-test", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var options = ParseSimpleOptions(args.Skip(1).ToArray());
            var noteId = options.TryGetValue("note", out var noteValue) && !string.IsNullOrWhiteSpace(noteValue)
                ? noteValue.Trim()
                : DefaultEvernoteDiagnosticNoteId;

            var configuredRoot = AppStateStore.Load().EvernoteLocalDbPath;
            var rootPath = options.TryGetValue("root", out var rootValue) && !string.IsNullOrWhiteSpace(rootValue)
                ? rootValue.Trim()
                : (configuredRoot ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new InvalidOperationException(
                    "Aucun dossier Evernote fourni. Utilise --root <chemin> ou configure le dossier dans l'application.");
            }

            var outputDirectory = options.TryGetValue("out", out var outputValue) && !string.IsNullOrWhiteSpace(outputValue)
                ? outputValue.Trim()
                : Path.Combine(Directory.GetCurrentDirectory(), "tmp", "evernote-note-test");

            var result = EvernoteLocalDbService.ExportSingleNoteDiagnostic(rootPath, noteId, outputDirectory);

            Console.WriteLine("Diagnostic Evernote termine.");
            Console.WriteLine($"Note: {result.NoteTitle} ({result.NoteGuid})");
            Console.WriteLine($"Notebook: {result.NotebookName} ({result.NotebookId})");
            Console.WriteLine($"Stack: {result.StackId}");
            Console.WriteLine($"DB: {result.DatabasePath}");
            Console.WriteLine($"ENML brut: {result.RawEnmlPath}");
            Console.WriteLine($"Markdown converti: {result.MarkdownPath}");
            Console.WriteLine($"Structure ENML: {result.StructureDumpPath}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Echec du diagnostic Evernote:");
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine(
                "  dotnet run -- --evernote-note-test [--note <guid>] [--root <evernote-root-path>] [--out <output-folder>]");
            Environment.ExitCode = 1;
        }

        return true;
    }

    private static Dictionary<string, string> ParseSimpleOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Argument invalide: {arg}");
            }

            if (i + 1 >= args.Length)
            {
                throw new InvalidOperationException($"Valeur manquante pour l'option {arg}");
            }

            var key = arg[2..].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException($"Option invalide: {arg}");
            }

            var value = args[++i];
            options[key] = value;
        }

        return options;
    }
}
