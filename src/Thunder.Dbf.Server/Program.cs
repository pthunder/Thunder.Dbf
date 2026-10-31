//         ____
//        /   /          ProjectThunder :: Thunder.Dbf
//       /   /__         The server: a safe JSON API in front of a legacy dBASE/FoxPro
//      /__    /         data folder, and the only process that writes to it.
//        /  /
//       / /             thunder-dbf-server                 run the API
//      //               thunder-dbf-server hash-key        make a new key and its config hash
//      '                thunder-dbf-server hash-key -      hash a key read from stdin

using Thunder.Dbf.Server;

if (args.Length > 0 && args[0] == "hash-key")
    return HashKeyCommand.Run(args[1..], Console.In, Console.Out, Console.Error);

var app = ThunderServer.Build(args);
app.Run();
return 0;

/// <summary>The entry point, public so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
