using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.Runtime.Internal.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// RequestMetrics creates its lock object and its errors list on first use. These tests pin the
    /// behavior that depended on them being created in the constructor.
    /// </summary>
    [TestClass]
    [TestCategory("Core")]
    public class RequestMetricsLazyInitTests
    {
        [TestMethod]
        public void NewMetrics_GetErrorsAndToStringWorkBeforeAnyUse()
        {
            Assert.AreEqual(string.Empty, new RequestMetrics().GetErrors());
            Assert.AreEqual(string.Empty, new RequestMetrics { IsEnabled = true }.GetErrors());
            Assert.AreEqual("Metrics logging disabled", new RequestMetrics().ToString());
            Assert.AreEqual(string.Empty, new RequestMetrics { IsEnabled = true }.ToString());
        }

        [TestMethod]
        public void Disabled_CsmAttemptLatencyErrorIsStillLogged()
        {
            var metrics = new RequestMetrics();
            metrics.StopEvent(Metric.CSMAttemptLatency);
            StringAssert.Contains(metrics.GetErrors(), "CSMAttemptLatency - Trying to stop event that has not been started;");
        }

        [TestMethod]
        public void Enabled_StartingTheSameEventTwiceLogsAnError()
        {
            var metrics = new RequestMetrics { IsEnabled = true };
            metrics.StartEvent(Metric.ClientExecuteTime);
            metrics.StartEvent(Metric.ClientExecuteTime);
            StringAssert.Contains(metrics.GetErrors(), "Logged 1 metrics errors: ");
            StringAssert.Contains(metrics.GetErrors(), "ClientExecuteTime - Starting multiple events for the same metric;");
        }

        [TestMethod]
        public void ConcurrentFirstUse_AllCallersShareOneLock()
        {
            // If two threads could each create their own lock object on first use, these unsynchronized
            // dictionary updates would lose increments or corrupt the dictionary.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                var metrics = new RequestMetrics { IsEnabled = true };
                const int perThread = 500;
                const int threads = 8;
                using (var start = new System.Threading.ManualResetEventSlim(false))
                {
                    var tasks = new Task[threads];
                    for (int t = 0; t < threads; t++)
                    {
                        tasks[t] = Task.Factory.StartNew(() =>
                        {
                            start.Wait();
                            for (int i = 0; i < perThread; i++)
                            {
                                metrics.IncrementCounter(Metric.HttpRequestTime);
                                metrics.AddProperty(Metric.ServiceName, i);
                            }
                        }, TaskCreationOptions.LongRunning);
                    }
                    start.Set();
                    Task.WaitAll(tasks);
                }

                Assert.AreEqual(threads * perThread, metrics.Counters[Metric.HttpRequestTime]);
                Assert.AreEqual(threads * perThread, metrics.Properties[Metric.ServiceName].Count);
            }
        }
    }
}
