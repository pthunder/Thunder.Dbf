//        ,--.
//       ( () )====[=|=|=]      ProjectThunder :: Thunder.Dbf
//        `--'                  hash-key: makes an API key and the SHA-256 hash that goes
//                              in appsettings.json. The key goes to the client; the hash
//                              goes in config; neither one can be turned back into the other.

namespace Thunder.Dbf.Server;

/// <summary>The <c>hash-key</c> command-line helper.</summary>
public static class HashKeyCommand
{
    private const int ShortKey = 24;

    /// <summary>
    /// With no argument, generates a random key and prints it with its hash. With "-", hashes one line
    /// from stdin (keeps the key out of shell history). With anything else, hashes that.
    /// </summary>
    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        switch (args)
        {
            case []:
            {
                var key = ApiKeyHasher.NewKey();
                stdout.WriteLine($"key:    {key}");
                stdout.WriteLine($"sha256: {ApiKeyHasher.HashHex(key)}");
                stdout.WriteLine();
                stdout.WriteLine("Give the key to the client (it goes in the X-Api-Key header) and put only the sha256 in");
                stdout.WriteLine("Thunder:ApiKeys in appsettings.json. The key isn't stored anywhere, so copy it now.");
                return 0;
            }

            case ["-"]:
            {
                var key = stdin.ReadLine()?.TrimEnd('\r', '\n');
                if (string.IsNullOrEmpty(key))
                {
                    stderr.WriteLine("No key on stdin.");
                    return 1;
                }
                Warn(key, stderr);
                stdout.WriteLine(ApiKeyHasher.HashHex(key));
                return 0;
            }

            case [var key] when !key.StartsWith('-'):
                Warn(key, stderr);
                stdout.WriteLine(ApiKeyHasher.HashHex(key));
                return 0;

            default:
                stderr.WriteLine("Usage: thunder-dbf-server hash-key [key | -]");
                stderr.WriteLine("  (no argument)  make a new random key and print it with its hash");
                stderr.WriteLine("  -              hash a key read from stdin");
                stderr.WriteLine("  key            hash this key (it will be in your shell history)");
                return 2;
        }
    }

    private static void Warn(string key, TextWriter stderr)
    {
        if (key.Length < ShortKey)
            stderr.WriteLine($"Warning: that key is only {key.Length} characters. Run hash-key with no argument for a strong random one.");
    }
}
