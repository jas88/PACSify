using System.Threading;
using System.Threading.Tasks;
using FellowOakDicom.Network;
using NUnit.Framework;

namespace PACSify.Test;

public class Tests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public async void Test1()
    {
        var cancelTokenSource = new CancellationTokenSource();
        cancelTokenSource.Cancel(true);
        var stoppingToken=cancelTokenSource.Token;
        using var server = DicomServerFactory.Create<StoreScp>(1104);
        await Task.Run(() => stoppingToken.WaitHandle.WaitOne(), stoppingToken);
        Assert.Pass();
    }
}