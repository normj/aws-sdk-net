using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.Util.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins AWSCredentials.TryGetCredentialsWithoutTask, which the async Signer and EndpointDiscoveryHandler use
    /// instead of awaiting GetCredentialsAsync: it must return exactly what awaiting GetCredentialsAsync would, and
    /// must decline (return false) whenever GetCredentialsAsync could do something else.
    /// </summary>
    [TestClass]
    [TestCategory("Core")]
    public class CredentialsWithoutTaskTests
    {
        private static readonly DateTime BaseTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void BasicCredentials_ReturnsTheSameCredentialsAsGetCredentials()
        {
            var credentials = new BasicAWSCredentials("AKID", "SECRET");

            Assert.IsTrue(credentials.TryGetCredentialsWithoutTask(out var result));
            Assert.AreSame(credentials.GetCredentials(), result);
            Assert.IsTrue(credentials.TryGetCredentialsWithoutTask(out var second));
            Assert.AreSame(result, second);
        }

        [TestMethod]
        public async Task SessionCredentials_MatchesGetCredentialsAsync()
        {
            var credentials = new SessionAWSCredentials("AKID", "SECRET", "TOKEN");

            Assert.IsTrue(credentials.TryGetCredentialsWithoutTask(out var result));
            Assert.AreSame(await credentials.GetCredentialsAsync(), result);
            Assert.AreEqual("TOKEN", result.Token);
        }

        [TestMethod]
        public void DerivedFromBasicCredentials_Declines()
        {
            // A derived class could override GetCredentialsAsync, so only the exact SDK types take the fast path.
            var credentials = new DerivedBasicCredentials();

            Assert.IsFalse(credentials.TryGetCredentialsWithoutTask(out var result));
            Assert.IsNull(result);
        }

        [TestMethod]
        public void CustomCredentialsWithDefaultGetCredentialsAsync_Declines()
        {
            var credentials = new SyncOnlyCredentials();

            Assert.IsFalse(credentials.TryGetCredentialsWithoutTask(out var result));
            Assert.IsNull(result);
            Assert.AreEqual(0, credentials.GetCredentialsCalls, "Must not call GetCredentials when the caller will await GetCredentialsAsync.");
        }

        [TestMethod]
        public void CustomCredentialsOverridingGetCredentialsAsync_Declines()
        {
            var credentials = new AsyncOverrideCredentials();

            Assert.IsFalse(credentials.TryGetCredentialsWithoutTask(out var result));
            Assert.IsNull(result);
            Assert.AreEqual(0, credentials.GetCredentialsCalls);
        }

        [TestMethod]
        public void AnonymousCredentials_Declines()
        {
            Assert.IsFalse(new AnonymousAWSCredentials().TryGetCredentialsWithoutTask(out _));
        }

        [TestMethod]
        public void Refreshing_ValidCredentials_ReturnsCachedWithoutGenerating()
        {
            var time = new MockTimeProvider { CorrectedUtcNow = BaseTimeUtc };
            var credentials = new CountingRefreshingCredentials(TimeSpan.FromMinutes(60), time);
            var initial = credentials.GetCredentials();
            Assert.AreEqual(1, credentials.Generated);

            time.CorrectedUtcNow = BaseTimeUtc.AddMinutes(30);
            Assert.IsTrue(credentials.TryGetCredentialsWithoutTask(out var result));
            Assert.AreSame(initial, result);
            Assert.AreEqual(1, credentials.Generated);
        }

        [TestMethod]
        public void Refreshing_NoCredentialsYet_Declines()
        {
            var credentials = new CountingRefreshingCredentials(TimeSpan.FromMinutes(60), new MockTimeProvider { CorrectedUtcNow = BaseTimeUtc });

            Assert.IsFalse(credentials.TryGetCredentialsWithoutTask(out var result));
            Assert.IsNull(result);
            Assert.AreEqual(0, credentials.Generated);
        }

        [TestMethod]
        [DataRow(59.5)]   // inside the 1 minute expiration buffer
        [DataRow(60.0)]
        [DataRow(120.0)]
        public async Task Refreshing_Expired_DeclinesAndGetCredentialsAsyncRefreshes(double minutes)
        {
            var time = new MockTimeProvider { CorrectedUtcNow = BaseTimeUtc };
            var credentials = new CountingRefreshingCredentials(TimeSpan.FromMinutes(60), time);
            var initial = credentials.GetCredentials();

            time.CorrectedUtcNow = BaseTimeUtc.AddMinutes(minutes);
            Assert.IsFalse(credentials.TryGetCredentialsWithoutTask(out _));
            Assert.AreEqual(1, credentials.Generated);

            var refreshed = await credentials.GetCredentialsAsync();
            Assert.AreNotSame(initial, refreshed);
            Assert.AreEqual(2, credentials.Generated);
            Assert.IsTrue(credentials.TryGetCredentialsWithoutTask(out var cached));
            Assert.AreSame(refreshed, cached);
        }

        [TestMethod]
        public async Task Refreshing_InPreemptWindow_DeclinesUntilABackgroundRefreshIsRunning()
        {
            var time = new MockTimeProvider { CorrectedUtcNow = BaseTimeUtc };
            var credentials = new CountingRefreshingCredentials(TimeSpan.FromMinutes(60), time)
            {
                PreemptExpiryTime = TimeSpan.FromMinutes(15),
            };
            var initial = credentials.GetCredentials();

            // Expiration is 59 minutes (60 minus the 1 minute buffer); the preempt window starts at 44 minutes.
            time.CorrectedUtcNow = BaseTimeUtc.AddMinutes(50);
            Assert.IsFalse(credentials.TryGetCredentialsWithoutTask(out _), "A preemptive refresh is due, so GetCredentialsAsync must run to start it.");

            credentials.CloseGate();
            try
            {
                // Starts the background refresh and returns the still-valid credentials.
                Assert.AreSame(initial, await credentials.GetCredentialsAsync());

                // While the background refresh runs, GetCredentialsAsync would return the current credentials too.
                Assert.IsTrue(credentials.TryGetCredentialsWithoutTask(out var during));
                Assert.AreSame(initial, during);
            }
            finally
            {
                credentials.OpenGate();
            }

            await credentials.WaitForGeneratedAsync(2);
        }

        private sealed class DerivedBasicCredentials : BasicAWSCredentials
        {
            public DerivedBasicCredentials() : base("AKID", "SECRET") { }

            public override Task<ImmutableCredentials> GetCredentialsAsync()
                => Task.FromResult(new ImmutableCredentials("derived", "secret", null));
        }

        private sealed class SyncOnlyCredentials : AWSCredentials
        {
            public int GetCredentialsCalls;

            public override ImmutableCredentials GetCredentials()
            {
                GetCredentialsCalls++;
                return new ImmutableCredentials("sync", "secret", null);
            }
        }

        private sealed class AsyncOverrideCredentials : AWSCredentials
        {
            public int GetCredentialsCalls;

            public override ImmutableCredentials GetCredentials()
            {
                GetCredentialsCalls++;
                return new ImmutableCredentials("sync", "secret", null);
            }

            public override Task<ImmutableCredentials> GetCredentialsAsync()
                => Task.FromResult(new ImmutableCredentials("async", "secret", null));
        }

        private sealed class MockTimeProvider : ITimeProvider
        {
            public DateTime CorrectedUtcNow { get; set; }
        }

        private sealed class CountingRefreshingCredentials : RefreshingAWSCredentials
        {
            private readonly TimeSpan _lifetime;
            private readonly ManualResetEventSlim _gate = new ManualResetEventSlim(initialState: true);
            private int _generated;

            public CountingRefreshingCredentials(TimeSpan lifetime, ITimeProvider timeProvider) : base(timeProvider)
            {
                _lifetime = lifetime;
            }

            public int Generated => Volatile.Read(ref _generated);

            public void CloseGate() => _gate.Reset();

            public void OpenGate() => _gate.Set();

            public async Task WaitForGeneratedAsync(int count)
            {
                for (int i = 0; i < 500 && Generated < count; i++)
                    await Task.Delay(10);
                Assert.AreEqual(count, Generated);
            }

            protected override CredentialsRefreshState GenerateNewCredentials()
            {
                _gate.Wait();
                var n = Interlocked.Increment(ref _generated);
                return new CredentialsRefreshState(new ImmutableCredentials("AKID", "SECRET", "token_" + n), _timeProvider.CorrectedUtcNow + _lifetime, _timeProvider);
            }

            protected override Task<CredentialsRefreshState> GenerateNewCredentialsAsync() => Task.Run(GenerateNewCredentials);
        }
    }
}
