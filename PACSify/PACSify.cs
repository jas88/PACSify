using System;
using System.Threading;
using System.Threading.Tasks;
using FellowOakDicom.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PACSify;

class Program : BackgroundService
{
    public static string Exe;
    static void Main(string[] args)
    { 
        Exe = args[0];
        UpdateFetcher.AutoUpdate();
        Host.CreateDefaultBuilder(args).UseWindowsService().UseSystemd()
            .ConfigureServices(s => s.AddHostedService<Program>());
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var server = DicomServerFactory.Create<StoreScp>(1104);
        await Task.Run(() => stoppingToken.WaitHandle.WaitOne(), stoppingToken);
    }
}