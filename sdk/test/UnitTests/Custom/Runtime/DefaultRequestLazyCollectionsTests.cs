using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using Amazon.Runtime.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// DefaultRequest creates its parameter collection, the Parameters facade and EventHeaders on first
    /// access, and AmazonServiceClient.ProcessRequestHandlers only builds event args when a handler is
    /// subscribed. These tests pin what callers could observe when both were created eagerly.
    /// </summary>
    [TestClass]
    [TestCategory("Core")]
    public class DefaultRequestLazyCollectionsTests
    {
        [TestMethod]
        public void Collections_AreNonNullEmptyAndStable()
        {
            var request = NewRequest();

            Assert.IsNotNull(request.ParameterCollection);
            Assert.IsNotNull(request.Parameters);
            Assert.IsNotNull(request.EventHeaders);
            Assert.AreEqual(0, request.ParameterCollection.Count);
            Assert.AreEqual(0, request.Parameters.Count);
            Assert.AreEqual(0, request.EventHeaders.Count);

            Assert.AreSame(request.ParameterCollection, request.ParameterCollection);
            Assert.AreSame(request.Parameters, request.Parameters);
            Assert.AreSame(request.EventHeaders, request.EventHeaders);
            Assert.IsInstanceOfType(request.Parameters, typeof(ParametersDictionaryFacade));
            Assert.IsInstanceOfType(request.EventHeaders, typeof(List<EventStreamHeader>));
        }

        [TestMethod]
        public void ParametersFacadeCreatedFirst_WrapsTheSameCollection()
        {
            var request = NewRequest();
            request.Parameters["b"] = "2";
            request.Parameters.Add("a", "1");

            var collection = request.ParameterCollection;
            Assert.AreEqual(2, collection.Count);
            Assert.AreEqual("1", ((StringParameterValue)collection["a"]).Value);

            collection.Add("c", "3");
            Assert.AreEqual("3", request.Parameters["c"]);
            Assert.AreEqual(3, request.Parameters.Count);
        }

        [TestMethod]
        public void ParameterCollectionCreatedFirst_IsSeenThroughTheFacade()
        {
            var request = NewRequest();
            request.ParameterCollection.Add("list", new List<string> { "x", "y" });
            request.ParameterCollection.Add("key", "value");

            Assert.AreEqual(2, request.Parameters.Count);
            Assert.AreEqual("value", request.Parameters["key"]);
            Assert.IsTrue(request.Parameters.ContainsKey("list"));
        }

        [TestMethod]
        public void EventHeaders_AreMutableAndKept()
        {
            var request = NewRequest();
            var header = new EventStreamHeader("h");
            request.EventHeaders.Add(header);
            Assert.AreEqual(1, request.EventHeaders.Count);
            Assert.AreSame(header, request.EventHeaders[0]);
        }

        [TestMethod]
        public void HasRequestData_StillSeesParameters()
        {
            var request = NewRequest();
            request.HttpMethod = "POST";
            Assert.IsFalse(request.HasRequestBody());
            request.Parameters["Action"] = "DoIt";
            Assert.IsTrue(request.HasRequestBody());
        }

        [TestMethod]
        public void ConcurrentFirstAccess_ReturnsOneInstance()
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                var request = NewRequest();
                var collections = new ParameterCollection[8];
                var facades = new IDictionary<string, string>[8];
                var headers = new IList<EventStreamHeader>[8];
                Parallel.For(0, 8, i =>
                {
                    facades[i] = request.Parameters;
                    collections[i] = request.ParameterCollection;
                    headers[i] = request.EventHeaders;
                });

                for (int i = 1; i < 8; i++)
                {
                    Assert.AreSame(collections[0], collections[i]);
                    Assert.AreSame(facades[0], facades[i]);
                    Assert.AreSame(headers[0], headers[i]);
                }

                request.Parameters["k"] = "v";
                Assert.IsTrue(request.ParameterCollection.ContainsKey("k"));
            }
        }

        [TestMethod]
        public void ProcessRequestHandlers_NoHandlers_DoesNothing()
        {
            using (var client = NewClient())
            {
                var request = NewRequest();
                client.RunRequestHandlers(NewContext(request));
                Assert.AreEqual(0, request.Headers.Count);
            }
        }

        [TestMethod]
        public void ProcessRequestHandlers_RequestHandler_GetsTheRequestCollections()
        {
            using (var client = NewClient())
            {
                var request = NewRequest();
                request.Parameters["p"] = "1";
                request.Headers["h"] = "2";

                WebServiceRequestEventArgs seen = null;
                ((IAmazonWebServiceRequest)request.OriginalRequest).AddBeforeRequestHandler((sender, e) =>
                {
                    Assert.AreSame(client, sender);
                    seen = (WebServiceRequestEventArgs)e;
                });

                client.RunRequestHandlers(NewContext(request));

                Assert.IsNotNull(seen);
                Assert.AreSame(request.ParameterCollection, seen.ParameterCollection);
                Assert.AreSame(request.Headers, seen.Headers);
                Assert.AreSame(request.OriginalRequest, seen.Request);
                Assert.AreEqual("test-service", seen.ServiceName);
            }
        }

        [TestMethod]
        public void ProcessRequestHandlers_ClientHandler_GetsTheRequestCollections()
        {
            using (var client = NewClient())
            {
                var request = NewRequest();
                WebServiceRequestEventArgs seen = null;
                client.BeforeRequestEvent += (sender, e) => seen = (WebServiceRequestEventArgs)e;

                client.RunRequestHandlers(NewContext(request));

                Assert.IsNotNull(seen);
                Assert.AreSame(request.ParameterCollection, seen.ParameterCollection);
                Assert.AreSame(request.Headers, seen.Headers);
            }
        }

        [TestMethod]
        public void ProcessRequestHandlers_RequestHandlerCanSubscribeTheClientHandler()
        {
            using (var client = NewClient())
            {
                var request = NewRequest();
                int clientCalls = 0;
                ((IAmazonWebServiceRequest)request.OriginalRequest).AddBeforeRequestHandler((sender, e) =>
                    client.BeforeRequestEvent += (s, args) => clientCalls++);

                client.RunRequestHandlers(NewContext(request));

                Assert.AreEqual(1, clientCalls);
            }
        }

        [TestMethod]
        public void ProcessRequestHandlers_NullRequest_StillThrows()
        {
            using (var client = NewClient())
            {
                Assert.ThrowsExactly<System.NullReferenceException>(() => client.RunRequestHandlers(NewContext(null)));
            }
        }

        private static DefaultRequest NewRequest() => new DefaultRequest(new MockAmazonWebServiceRequest(), "test-service");

        private static ExecutionContext NewContext(IRequest request)
            => new ExecutionContext(new RequestContext(false) { Request = request }, new ResponseContext());

        private static HandlersClient NewClient()
            => new HandlersClient(new BasicAWSCredentials("AKIDEXAMPLE", "secret"), new MockClientConfig { RegionEndpoint = RegionEndpoint.USEast1 });

        private sealed class HandlersClient : AmazonServiceClient
        {
            public HandlersClient(AWSCredentials credentials, ClientConfig config) : base(credentials, config) { }

            public void RunRequestHandlers(IExecutionContext context) => ProcessRequestHandlers(context);
        }
    }
}
