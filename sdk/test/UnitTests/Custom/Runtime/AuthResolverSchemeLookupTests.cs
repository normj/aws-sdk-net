using Amazon.Runtime;
using Amazon.Runtime.Credentials.Internal;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins how BaseAuthResolverHandler maps each auth scheme option to a supported scheme: options are
    /// tried in order, an unsupported scheme id is skipped, and the first supported one decides the signer
    /// and the chosen option.
    /// </summary>
    [TestClass]
    public class AuthResolverSchemeLookupTests : RuntimePipelineTestBase
    {
        private sealed class TestAuthResolverHandler : BaseAuthResolverHandler
        {
            public TestAuthResolverHandler()
            {
                Logger = Amazon.Runtime.Internal.Util.Logger.GetLogger(typeof(AuthResolverSchemeLookupTests));
            }

            public List<IAuthSchemeOption> Options { get; set; }

            protected override List<IAuthSchemeOption> ResolveAuthOptions(IExecutionContext executionContext) => Options;
        }

        private static List<IAuthSchemeOption> Options(params string[] schemeIds)
        {
            var options = new List<IAuthSchemeOption>();
            foreach (var id in schemeIds)
                options.Add(new AuthSchemeOption { SchemeId = id });
            return options;
        }

        private IExecutionContext CreateContextWithCredentials()
        {
            var context = CreateTestContext();
            ((RequestContext)context.RequestContext).ExplicitAWSCredentials = new BasicAWSCredentials("AKIDEXAMPLE", "secret");
            return context;
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        [DataRow(false)]
        [DataRow(true)]
        public async Task SigV4Option_SelectsAWS4Signer(bool async)
        {
            var options = Options("aws.auth#sigv4");
            var context = CreateContextWithCredentials();
            var handler = new TestAuthResolverHandler { InnerHandler = new MockHandler(), Options = options };

            if (async) await handler.InvokeAsync<AmazonWebServiceResponse>(context);
            else handler.InvokeSync(context);

            Assert.IsInstanceOfType(context.RequestContext.Signer, typeof(AWS4Signer));
            Assert.AreSame(options[0], context.RequestContext.Request.ChosenAuthScheme);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        [DataRow(false)]
        [DataRow(true)]
        public async Task UnsupportedOptionFirst_IsSkipped(bool async)
        {
            var options = Options("custom#unsupported", "AWS.AUTH#SIGV4", "aws.auth#sigv4");
            var context = CreateContextWithCredentials();
            var handler = new TestAuthResolverHandler { InnerHandler = new MockHandler(), Options = options };

            if (async) await handler.InvokeAsync<AmazonWebServiceResponse>(context);
            else handler.InvokeSync(context);

            // Scheme ids are compared ordinally, so the upper-case id does not match.
            Assert.IsInstanceOfType(context.RequestContext.Signer, typeof(AWS4Signer));
            Assert.AreSame(options[2], context.RequestContext.Request.ChosenAuthScheme);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        [DataRow(false)]
        [DataRow(true)]
        public async Task NoAuthOption_SelectsNullSignerAndAnonymousIdentity(bool async)
        {
            var options = Options("smithy.api#noAuth", "aws.auth#sigv4");
            var context = CreateTestContext();
            var handler = new TestAuthResolverHandler { InnerHandler = new MockHandler(), Options = options };

            if (async) await handler.InvokeAsync<AmazonWebServiceResponse>(context);
            else handler.InvokeSync(context);

            Assert.IsInstanceOfType(context.RequestContext.Signer, typeof(NullSigner));
            Assert.IsInstanceOfType(context.RequestContext.Identity, typeof(AnonymousAWSCredentials));
            Assert.AreSame(options[0], context.RequestContext.Request.ChosenAuthScheme);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        [DataRow(false)]
        [DataRow(true)]
        public async Task OnlyUnsupportedOptions_Throws(bool async)
        {
            var context = CreateContextWithCredentials();
            var handler = new TestAuthResolverHandler { InnerHandler = new MockHandler(), Options = Options("custom#a", "custom#b") };

            AmazonClientException exception = null;
            try
            {
                if (async) await handler.InvokeAsync<AmazonWebServiceResponse>(context);
                else handler.InvokeSync(context);
            }
            catch (AmazonClientException e)
            {
                exception = e;
            }

            Assert.IsNotNull(exception);
            StringAssert.Contains(exception.Message, "Could not determine which authentication scheme to use");
            Assert.IsNull(context.RequestContext.Request.ChosenAuthScheme);
        }

        private sealed class MockHandler : PipelineHandler
        {
            public override void InvokeSync(IExecutionContext executionContext) { }

            public override Task<T> InvokeAsync<T>(IExecutionContext executionContext) => Task.FromResult<T>(null);
        }
    }
}
