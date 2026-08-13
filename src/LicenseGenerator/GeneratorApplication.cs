namespace LicenseGenerator;

internal static class GeneratorApplication
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || CommandLine.HasFlag(args, "--help"))
            {
                GeneratorUsage.Print();
                return args.Length == 0 ? 2 : 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "keygen" => KeyPairGenerator.Generate(args[1..]),
                "sign" => LicenseSigner.Sign(args[1..]),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        GeneratorUsage.Print();
        return 2;
    }
}
