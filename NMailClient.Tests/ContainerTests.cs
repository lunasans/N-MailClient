using Microsoft.Extensions.DependencyInjection;
using NMailClient;
using NMailClient.Models;
using NMailClient.Services;
using NMailClient.ViewModels;
using NMailClient.Views;
using Xunit;

namespace NMailClient.Tests;

/// <summary>
/// Prüft die DI-Registrierungen. Fenster werden bewusst nicht aufgelöst – das
/// bräuchte einen laufenden WPF-Host; geprüft wird, dass sie registriert sind.
/// </summary>
public class ContainerTests
{
    [Fact]
    public async Task ResolvesCoreServices()
    {
        await using var sp = App.BuildContainer();

        Assert.NotNull(sp.GetRequiredService<SettingsStore>());
        Assert.NotNull(sp.GetRequiredService<MailServiceRegistry>());
    }

    [Fact]
    public async Task SharedStateIsSingleton()
    {
        await using var sp = App.BuildContainer();

        // Zwei Instanzen der Registry hiessen: zwei IMAP-Verbindungen je Konto und
        // eine wirkungslose Auth-Sperre.
        Assert.Same(sp.GetRequiredService<MailServiceRegistry>(),
                    sp.GetRequiredService<MailServiceRegistry>());
        Assert.Same(sp.GetRequiredService<SettingsStore>(),
                    sp.GetRequiredService<SettingsStore>());
    }

    [Fact]
    public async Task WindowsAndViewModelAreRegistered()
    {
        await using var sp = App.BuildContainer();
        var probe = sp.GetRequiredService<IServiceProviderIsService>();

        // Nur die Registrierung prüfen, nicht auflösen: MainViewModel würde im
        // Konstruktor Konten laden und damit eine IMAP-Verbindung aufbauen.
        Assert.True(probe.IsService(typeof(MainViewModel)));
        Assert.True(probe.IsService(typeof(MainWindow)));
        Assert.True(probe.IsService(typeof(SettingsWindow)));
    }

    [Fact]
    public async Task FactoriesAreRegistered()
    {
        await using var sp = App.BuildContainer();

        Assert.NotNull(sp.GetRequiredService<Func<SettingsWindow>>());
        Assert.NotNull(sp.GetRequiredService<Func<Account, ComposeRequest, ComposeWindow>>());
    }
}
