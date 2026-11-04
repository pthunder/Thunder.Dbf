//      _______
//     | .DBF  |   ProjectThunder :: Thunder.Dbf CRM sample
//     |_______|   iOS start-up: hands over to MauiProgram.

using Foundation;

namespace ThunderDbfMaui;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
