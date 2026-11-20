/*
 * Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 *
 *  http://aws.amazon.com/apache2.0
 *
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Credentials.Internal;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests.Runtime
{
    /// <summary>
    /// Pins how the auth resolver handler matches the resolved auth scheme options against the schemes it
    /// supports: options are tried in order, unknown or null scheme ids are skipped, and the first supported
    /// option wins. Covers both the sync and async entry points.
    /// </summary>
    [TestClass]
    public class AuthResolverSchemeSelectionTests
    {
        private const string SigV4 = "aws.auth#sigv4";
        private const string NoAuth = "smithy.api#noAuth";
        private const string Unknown = "example.com#unknown";

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task SigV4OptionSelectsSigV4SchemeAndSigner(bool useAsync)
        {
            var context = await RunAsync(useAsync, SigV4);

            Assert.AreEqual(SigV4, context.RequestContext.Request.ChosenAuthScheme.SchemeId);
            Assert.IsInstanceOfType(context.RequestContext.Signer, typeof(AWS4Signer));
            Assert.IsInstanceOfType(context.RequestContext.Identity, typeof(BasicAWSCredentials));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task UnsupportedAndNullOptionsAreSkipped(bool useAsync)
        {
            var context = await RunAsync(useAsync, Unknown, null, SigV4, NoAuth);

            Assert.AreEqual(SigV4, context.RequestContext.Request.ChosenAuthScheme.SchemeId);
            Assert.IsInstanceOfType(context.RequestContext.Signer, typeof(AWS4Signer));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FirstSupportedOptionWins(bool useAsync)
        {
            var context = await RunAsync(useAsync, NoAuth, SigV4);

            Assert.AreEqual(NoAuth, context.RequestContext.Request.ChosenAuthScheme.SchemeId);
            Assert.IsInstanceOfType(context.RequestContext.Signer, typeof(NullSigner));
            Assert.IsInstanceOfType(context.RequestContext.Identity, typeof(AnonymousAWSCredentials));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task SchemeIdsAreMatchedOrdinally(bool useAsync)
        {
            // Differs from a supported id only by case, so it must not match.
            var ex = await Assert.ThrowsExactlyAsync<AmazonClientException>(() => RunAsync(useAsync, "AWS.AUTH#SIGV4"));
            StringAssert.StartsWith(ex.Message, "Could not determine which authentication scheme to use");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task NoSupportedOptionThrows(bool useAsync)
        {
            var ex = await Assert.ThrowsExactlyAsync<AmazonClientException>(() => RunAsync(useAsync, Unknown, null));
            StringAssert.StartsWith(ex.Message, "Could not determine which authentication scheme to use");
        }

        private static async Task<IExecutionContext> RunAsync(bool useAsync, params string[] schemeIds)
        {
            var options = schemeIds.Select(id => (IAuthSchemeOption)new AuthSchemeOption { SchemeId = id }).ToList();
            var handler = new TestAuthResolver(options) { Logger = Amazon.Runtime.Internal.Util.Logger.GetLogger(typeof(AuthResolverSchemeSelectionTests)) };
            var requestContext = new RequestContext(false)
            {
                ClientConfig = new TestClientConfig { DefaultAWSCredentials = new BasicAWSCredentials("access", "secret") },
                OriginalRequest = new TestRequest(),
            };
            requestContext.Request = new DefaultRequest(requestContext.OriginalRequest, "TestService");
            var context = new ExecutionContext(requestContext, new ResponseContext());

            if (useAsync)
                await handler.RunAsync(context);
            else
                handler.Run(context);

            return context;
        }

        private sealed class TestRequest : AmazonWebServiceRequest { }

        private sealed class TestAuthResolver : BaseAuthResolverHandler
        {
            private readonly List<IAuthSchemeOption> _options;

            public TestAuthResolver(List<IAuthSchemeOption> options) => _options = options;

            public void Run(IExecutionContext context) => PreInvoke(context);

            public Task RunAsync(IExecutionContext context) => PreInvokeAsync(context);

            protected override List<IAuthSchemeOption> ResolveAuthOptions(IExecutionContext executionContext) => _options;
        }

        private sealed class TestClientConfig : ClientConfig
        {
            public TestClientConfig() : base(new Provider()) { }

            public override string RegionEndpointServiceName => "test";
            public override string ServiceVersion => "2020-01-01";
            public override string UserAgent => "test";

            public override Amazon.Runtime.Endpoints.Endpoint DetermineServiceOperationEndpoint(Amazon.Runtime.Endpoints.ServiceOperationEndpointParameters parameters)
                => new Amazon.Runtime.Endpoints.Endpoint("https://example.com");

            private sealed class Provider : IDefaultConfigurationProvider
            {
                public IDefaultConfiguration GetDefaultConfiguration(RegionEndpoint clientRegion, DefaultConfigurationMode? requestedConfigurationMode = null)
                    => new DefaultConfiguration();
            }
        }
    }
}
