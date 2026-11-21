using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// The async Signer reads credentials without a task only when that returns what GetCredentialsAsync would.
    /// These tests pin which credentials it signs with, using the session token header as the observable result.
    /// </summary>
    [TestClass]
    [TestCategory("Core")]
    public class SignerAsyncCredentialsTests : RuntimePipelineTestBase
    {
        [TestMethod]
        public async Task SessionCredentials_TokenComesFromGetCredentials()
        {
            var context = await SignAsync(new SessionAWSCredentials("AKID", "SECRET", "session-token"));

            Assert.AreEqual("session-token", context.RequestContext.Request.Headers[HeaderKeys.XAmzSecurityTokenHeader]);
        }

        [TestMethod]
        public async Task OverriddenGetCredentialsAsync_IsAwaited()
        {
            var credentials = new AsyncTokenCredentials();
            var context = await SignAsync(credentials);

            Assert.AreEqual(1, credentials.GetCredentialsAsyncCalls);
            Assert.AreEqual(0, credentials.GetCredentialsCalls);
            Assert.AreEqual("async-token", context.RequestContext.Request.Headers[HeaderKeys.XAmzSecurityTokenHeader]);
        }

        [TestMethod]
        public async Task DerivedFromSessionCredentialsWithOverride_IsAwaited()
        {
            var credentials = new DerivedSessionCredentials();
            var context = await SignAsync(credentials);

            Assert.AreEqual(1, credentials.GetCredentialsAsyncCalls);
            Assert.AreEqual("derived-token", context.RequestContext.Request.Headers[HeaderKeys.XAmzSecurityTokenHeader]);
        }

        private async Task<IExecutionContext> SignAsync(AWSCredentials credentials)
        {
            var pipeline = new RuntimePipeline(new MockHandler());
            pipeline.AddHandler(new Signer());

            var signer = new MockSigner();
            var context = CreateTestContext(signer);
            context.RequestContext.Identity = credentials;
            await pipeline.InvokeAsync<AmazonWebServiceResponse>(context);

            Assert.IsTrue(context.RequestContext.IsSigned);
            Assert.AreEqual(1, signer.SignCount);
            return context;
        }

        private sealed class AsyncTokenCredentials : AWSCredentials
        {
            public int GetCredentialsCalls;
            public int GetCredentialsAsyncCalls;

            public override ImmutableCredentials GetCredentials()
            {
                GetCredentialsCalls++;
                return new ImmutableCredentials("AKID", "SECRET", "sync-token");
            }

            public override Task<ImmutableCredentials> GetCredentialsAsync()
            {
                GetCredentialsAsyncCalls++;
                return Task.FromResult(new ImmutableCredentials("AKID", "SECRET", "async-token"));
            }
        }

        private sealed class DerivedSessionCredentials : SessionAWSCredentials
        {
            public int GetCredentialsAsyncCalls;

            public DerivedSessionCredentials() : base("AKID", "SECRET", "base-token") { }

            public override Task<ImmutableCredentials> GetCredentialsAsync()
            {
                GetCredentialsAsyncCalls++;
                return Task.FromResult(new ImmutableCredentials("AKID", "SECRET", "derived-token"));
            }
        }
    }
}
