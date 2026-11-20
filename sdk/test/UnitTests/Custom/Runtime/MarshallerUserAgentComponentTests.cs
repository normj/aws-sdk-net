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
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Transform;
using Amazon.Runtime.Telemetry;
using Amazon.Util.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the User-Agent components the Marshaller handler adds for every request: the sanitized client
    /// User-Agent, the sanitized app id, the sync/async marker and the collection-initialization flag. The
    /// handler reuses the sanitized strings between requests, so these tests check that a changed value is
    /// never served from a stale cache.
    /// </summary>
    [TestClass]
    public class MarshallerUserAgentComponentTests
    {
        private bool _initializeCollections;

        [TestInitialize]
        public void Initialize() => _initializeCollections = AWSConfigs.InitializeCollections;

        [TestCleanup]
        public void Cleanup() => AWSConfigs.InitializeCollections = _initializeCollections;

        [TestMethod]
        [DataRow("aws-sdk-dotnet-coreclr/4.0.0.0 ua/2.1 os/Linux#6.1 md/test lang/.NET_Core#10.0", null, true, true)]
        [DataRow("aws-sdk-dotnet-coreclr/4.0.0.0 ua/2.1 os/Linux#6.1 md/test lang/.NET_Core#10.0", "my-app", false, false)]
        [DataRow("bad\"chars(here)@[x]=y{z}<a>?,b;c:d\\e\u00e9\u4e2d", "app id with (bad) chars;", true, false)]
        [DataRow("", "", false, true)]
        public void ComponentsMatchUncachedFormat(string userAgent, string appId, bool isAsync, bool initializeCollections)
        {
            AWSConfigs.InitializeCollections = initializeCollections;
            var config = new TestClientConfig { UserAgentValue = userAgent, ClientAppId = appId };

            // The first request fills the cache and the second is served from it; both must match.
            for (int i = 0; i < 2; i++)
            {
                Assert.AreEqual(ExpectedComponents(userAgent, appId, isAsync, initializeCollections), RunPreInvoke(config, isAsync));
            }
        }

        [TestMethod]
        public void ChangedUserAgentIsNotServedFromCache()
        {
            var config = new TestClientConfig { UserAgentValue = "first/1.0" };
            Assert.AreEqual(ExpectedComponents("first/1.0", null, true, AWSConfigs.InitializeCollections), RunPreInvoke(config, true));

            config.UserAgentValue = "second(2.0)";
            Assert.AreEqual(ExpectedComponents("second(2.0)", null, true, AWSConfigs.InitializeCollections), RunPreInvoke(config, true));

            // A new string instance with the same contents gives the same component.
            config.UserAgentValue = new string("second(2.0)".ToCharArray());
            Assert.AreEqual(ExpectedComponents("second(2.0)", null, true, AWSConfigs.InitializeCollections), RunPreInvoke(config, true));
        }

        [TestMethod]
        public void ChangedClientAppIdIsNotServedFromCache()
        {
            var config = new TestClientConfig { UserAgentValue = "ua/1.0", ClientAppId = "app-one" };
            Assert.AreEqual(ExpectedComponents("ua/1.0", "app-one", false, AWSConfigs.InitializeCollections), RunPreInvoke(config, false));

            config.ClientAppId = "app(two)";
            Assert.AreEqual(ExpectedComponents("ua/1.0", "app(two)", false, AWSConfigs.InitializeCollections), RunPreInvoke(config, false));

            config.ClientAppId = string.Empty;
            Assert.AreEqual(ExpectedComponents("ua/1.0", string.Empty, false, AWSConfigs.InitializeCollections), RunPreInvoke(config, false));
        }

        [TestMethod]
        public void AppIdEqualToUserAgentDoesNotShareTheUserAgentComponent()
        {
            var config = new TestClientConfig { UserAgentValue = "same(value)", ClientAppId = "same(value)" };
            for (int i = 0; i < 2; i++)
            {
                Assert.AreEqual(ExpectedComponents("same(value)", "same(value)", true, AWSConfigs.InitializeCollections), RunPreInvoke(config, true));
            }
        }

        [TestMethod]
        public void ConfigsDoNotShareCachedComponents()
        {
            var first = new TestClientConfig { UserAgentValue = "first/1.0", ClientAppId = "first-app" };
            var second = new TestClientConfig { UserAgentValue = "second/2.0", ClientAppId = "second-app" };

            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(ExpectedComponents("first/1.0", "first-app", true, AWSConfigs.InitializeCollections), RunPreInvoke(first, true));
                Assert.AreEqual(ExpectedComponents("second/2.0", "second-app", false, AWSConfigs.InitializeCollections), RunPreInvoke(second, false));
            }
        }

        [TestMethod]
        public void NullUserAgentThrowsArgumentNullExceptionBeforeAndAfterCaching()
        {
            var config = new TestClientConfig { UserAgentValue = null };
            Assert.ThrowsExactly<ArgumentNullException>(() => RunPreInvoke(config, true));

            config.UserAgentValue = "ua/1.0";
            RunPreInvoke(config, true);

            config.UserAgentValue = null;
            Assert.ThrowsExactly<ArgumentNullException>(() => RunPreInvoke(config, true));
        }

        [TestMethod]
        public void ClientConfigThatIsNotAClientConfigSubclassGetsTheSameComponents()
        {
            var mock = new Mock<IClientConfig>();
            mock.SetupGet(c => c.TelemetryProvider).Returns(AWSConfigs.TelemetryProvider);
            mock.SetupGet(c => c.ResponseChecksumValidation).Returns(ResponseChecksumValidation.WHEN_SUPPORTED);
            mock.SetupGet(c => c.UserAgent).Returns("mock(ua)/1.0");
            mock.SetupGet(c => c.ClientAppId).Returns("mock app");

            var other = new TestClientConfig { UserAgentValue = "other/1.0", ClientAppId = "other-app" };

            for (int i = 0; i < 2; i++)
            {
                Assert.AreEqual(ExpectedComponents("mock(ua)/1.0", "mock app", true, AWSConfigs.InitializeCollections), RunPreInvoke(mock.Object, true));
                Assert.AreEqual(ExpectedComponents("other/1.0", "other-app", true, AWSConfigs.InitializeCollections), RunPreInvoke(other, true));
            }

            mock.SetupGet(c => c.UserAgent).Returns("mock(ua)/2.0");
            Assert.AreEqual(ExpectedComponents("mock(ua)/2.0", "mock app", true, AWSConfigs.InitializeCollections), RunPreInvoke(mock.Object, true));
        }

        [TestMethod]
        public void ClientConfigThatIsNotAClientConfigSubclassKeepsAppIdPrefixWhenAppIdEqualsUserAgent()
        {
            var mock = new Mock<IClientConfig>();
            mock.SetupGet(c => c.TelemetryProvider).Returns(AWSConfigs.TelemetryProvider);
            mock.SetupGet(c => c.ResponseChecksumValidation).Returns(ResponseChecksumValidation.WHEN_SUPPORTED);
            mock.SetupGet(c => c.UserAgent).Returns("same(value)");
            mock.SetupGet(c => c.ClientAppId).Returns("same(value)");

            for (int i = 0; i < 2; i++)
            {
                Assert.AreEqual(ExpectedComponents("same(value)", "same(value)", true, AWSConfigs.InitializeCollections), RunPreInvoke(mock.Object, true));
            }

            // One non-ClientConfig config's app id must not be served as another's User-Agent.
            var other = new Mock<IClientConfig>();
            other.SetupGet(c => c.TelemetryProvider).Returns(AWSConfigs.TelemetryProvider);
            other.SetupGet(c => c.ResponseChecksumValidation).Returns(ResponseChecksumValidation.WHEN_SUPPORTED);
            other.SetupGet(c => c.UserAgent).Returns("foo");
            mock.SetupGet(c => c.ClientAppId).Returns("foo");
            Assert.AreEqual(ExpectedComponents("same(value)", "foo", true, AWSConfigs.InitializeCollections), RunPreInvoke(mock.Object, true));
            Assert.AreEqual(ExpectedComponents("foo", null, true, AWSConfigs.InitializeCollections), RunPreInvoke(other.Object, true));
        }

        /// <summary>
        /// The components string as the handler built it before caching was added.
        /// </summary>
        private static string ExpectedComponents(string userAgent, string appId, bool isAsync, bool initializeCollections)
        {
            var details = new Amazon.Runtime.Internal.UserAgent.UserAgentDetails();
            details.AddUserAgentComponent(InternalSDKUtils.ReplaceInvalidUserAgentCharacters(userAgent));
            if (!string.IsNullOrEmpty(appId))
                details.AddUserAgentComponent($"app/{InternalSDKUtils.ReplaceInvalidUserAgentCharacters(appId)}");
            details.AddUserAgentComponent($"md/{(isAsync ? "ClientAsync" : "ClientSync")}");
            details.AddUserAgentComponent($"cfg/init-coll#{(initializeCollections ? '1' : '0')}");
            return details.GetCustomUserAgentComponents();
        }

        private static string RunPreInvoke(IClientConfig config, bool isAsync)
        {
            var requestContext = new RequestContext(false)
            {
                ClientConfig = config,
                OriginalRequest = new TestRequest(),
                Marshaller = new TestMarshaller(),
                IsAsync = isAsync,
            };
            TestMarshallerHandler.Run(new ExecutionContext(requestContext, new ResponseContext()));
            return requestContext.UserAgentDetails.GetCustomUserAgentComponents();
        }

        private sealed class TestRequest : AmazonWebServiceRequest { }

        private sealed class TestMarshaller : IMarshaller<IRequest, AmazonWebServiceRequest>
        {
            public IRequest Marshall(AmazonWebServiceRequest input) => new DefaultRequest(input, "TestService");
        }

        private sealed class TestMarshallerHandler : Marshaller
        {
            public static void Run(IExecutionContext context) => PreInvoke(context);
        }

        private sealed class TestClientConfig : ClientConfig
        {
            public TestClientConfig() : base(new Provider()) { }

            public string UserAgentValue { get; set; }

            public override string RegionEndpointServiceName => "test";
            public override string ServiceVersion => "2020-01-01";
            public override string UserAgent => UserAgentValue;

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
