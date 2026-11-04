//      _______
//     | .DBF  |   ProjectThunder :: Thunder.Dbf CRM sample
//     |_______|   The app: one window, with the contact list in a navigation shell.

using Microsoft.Extensions.DependencyInjection;

namespace ThunderDbfMaui;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}