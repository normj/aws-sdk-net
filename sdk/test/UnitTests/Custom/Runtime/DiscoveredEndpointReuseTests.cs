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
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the endpoint EndpointDiscoveryHandler sets from a discovered address (the handler reuses the Uri parsed
    /// for the previous request's address) and the endpoint discovery cache key.
    /// </summary>
    [TestClass]
    [TestCategory("UnitTest")]
    [TestCategory("Runtime")]
    public class DiscoveredEndpointReuseTests
    {
        private sealed class TestDiscoveryData : EndpointDiscoveryDataBase
        {
            public TestDiscoveryData(bool required) : base(required) { }

            public void SetIdentifiers(SortedDictionary<string, string> identifiers)
            {
                Identifiers = identifiers;
            }
        }

        private sealed class TestDiscoveryEndpoint : DiscoveryEndpointBase
        {
            public TestDiscoveryEndpoint(string address) : base(address, 60) { }
        }

        private sealed class TestRequest : AmazonWebServiceRequest { }

        /// <summary>
        /// The base constructor rejects null or empty credentials, but the getter is virtual, so a subclass can still
        /// return them.
        /// </summary>
        private sealed class OverriddenCredentialsContext : EndpointOperationContextBase
        {
            private readonly string _credentials;

            public OverriddenCredentialsContext(string credentials, string operationName, EndpointDiscoveryDataBase data)
                : base("placeholder", operationName, data, false, null)
            {
                _credentials = credentials;
            }

            public override string CustomerCredentials => _credentials;
        }

        /// <summary>
        /// The cache key exactly as it was built before: credentials, then ".{operation}" and ".{value}" per
        /// identifier when there are identifiers.
        /// </summary>
        private static string LegacyCacheKey(EndpointOperationContextBase context)
        {
            var cacheKeyBuilder = new StringBuilder();
            cacheKeyBuilder.Append(context.CustomerCredentials);
            var identifiers = context.EndpointDiscoveryData.Identifiers;
            if (identifiers != null && identifiers.Count > 0)
            {
                cacheKeyBuilder.Append(string.Format(CultureInfo.InvariantCulture, ".{0}", context.OperationName));
                foreach (var identifier in identifiers)
                {
                    cacheKeyBuilder.Append(string.Format(CultureInfo.InvariantCulture, ".{0}", identifier.Value));
                }
            }

            return cacheKeyBuilder.ToString();
        }

        [TestMethod]
        public void CacheKeyMatchesTheLegacyFormat()
        {
            var identifierSets = new List<SortedDictionary<string, string>>
            {
                null,
                new SortedDictionary<string, string>(),
                new SortedDictionary<string, string> { { "TableName", "my-table" } },
                new SortedDictionary<string, string> { { "b", "2" }, { "a", "1" }, { "c", null } },
                new SortedDictionary<string, string> { { "x", "" } },
            };

            foreach (var credentials in new[] { "AKIDEXAMPLE", null, "", "with.dot" })
            {
                foreach (var operationName in new[] { "DescribeTable", null, "" })
                {
                    foreach (var identifiers in identifierSets)
                    {
                        var data = new TestDiscoveryData(true);
                        data.SetIdentifiers(identifiers == null ? null : new SortedDictionary<string, string>(identifiers));
                        EndpointOperationContextBase context = string.IsNullOrEmpty(credentials)
                            ? new OverriddenCredentialsContext(credentials, operationName, data)
                            : new EndpointOperationContext(credentials, operationName, data, false, null);

                        var expected = LegacyCacheKey(context);
                        var actual = EndpointDiscoveryResolverBase.BuildEndpointDiscoveryCacheKey(context);
                        Assert.AreEqual(expected, actual,
                            $"credentials={credentials ?? "null"} operation={operationName ?? "null"} identifiers={(identifiers == null ? "null" : identifiers.Count.ToString())}");
                        Assert.IsNotNull(actual);
                    }
                }
            }
        }

        [TestMethod]
        public void DiscoveredUriEqualsParsedAddress()
        {
            var addresses = new[]
            {
                "https://ingest-cell2.timestream.us-west-2.amazonaws.com",
                "https://ingest-cell2.timestream.us-west-2.amazonaws.com",
                "https://ingest-cell1.timestream.us-west-2.amazonaws.com",
                "https://ingest-cell2.timestream.us-west-2.amazonaws.com",
                "https://INGEST-CELL2.timestream.us-west-2.amazonaws.com",
                "https://ingest-cell2.timestream.us-west-2.amazonaws.com:8443/",
                "http://ingest-cell2.timestream.us-west-2.amazonaws.com/path",
                "https://ingest-cell2.timestream.us-west-2.amazonaws.com",
            };

            foreach (var address in addresses)
            {
                var expected = new Uri(address);
                var actual = EndpointDiscoveryHandler.GetDiscoveredEndpointUri(new string(address.ToCharArray()));
                Assert.AreEqual(expected, actual);
                Assert.AreEqual(expected.OriginalString, actual.OriginalString);
                Assert.AreEqual(expected.AbsoluteUri, actual.AbsoluteUri);
                Assert.AreEqual(expected.ToString(), actual.ToString());
                Assert.AreEqual(expected.Host, actual.Host);
                Assert.AreEqual(expected.Port, actual.Port);
                Assert.AreEqual(expected.Scheme, actual.Scheme);
            }

            Assert.ThrowsExactly<ArgumentNullException>(() => EndpointDiscoveryHandler.GetDiscoveredEndpointUri(null));
            Assert.ThrowsExactly<UriFormatException>(() => EndpointDiscoveryHandler.GetDiscoveredEndpointUri("not a uri"));
            Assert.ThrowsExactly<UriFormatException>(() => EndpointDiscoveryHandler.GetDiscoveredEndpointUri("not a uri"));
            Assert.AreEqual(new Uri("https://a.example.com"), EndpointDiscoveryHandler.GetDiscoveredEndpointUri("https://a.example.com"));
        }

        [TestMethod]
        public void DiscoverEndpointsSetsTheFirstNonNullAddress()
        {
            var endpointLists = new List<IList<DiscoveryEndpointBase>>
            {
                new List<DiscoveryEndpointBase> { new TestDiscoveryEndpoint("ingest-cell2.timestream.us-west-2.amazonaws.com") },
                new List<DiscoveryEndpointBase> { new TestDiscoveryEndpoint("ingest-cell2.timestream.us-west-2.amazonaws.com") },
                new List<DiscoveryEndpointBase> { new TestDiscoveryEndpoint(null), new TestDiscoveryEndpoint("https://ingest-cell3.timestream.us-west-2.amazonaws.com") },
                new List<DiscoveryEndpointBase> { new TestDiscoveryEndpoint("ingest-cell2.timestream.us-west-2.amazonaws.com") },
            };

            foreach (var endpoints in endpointLists)
            {
                var regional = new Uri("https://ingest.timestream.us-west-2.amazonaws.com");
                var requestContext = CreateRequestContext(regional, endpoints);

                EndpointDiscoveryHandler.DiscoverEndpoints(requestContext, false, new ImmutableCredentials("AKIDEXAMPLE", "SECRET", null));

                var address = endpoints.First(e => e.Address != null).Address;
                Assert.AreEqual(new Uri(address), requestContext.Request.Endpoint);
                Assert.AreEqual(new Uri(address).AbsoluteUri, requestContext.Request.Endpoint.AbsoluteUri);
            }

            // Only null addresses: the regional endpoint is kept.
            var regionalOnly = new Uri("https://ingest.timestream.us-west-2.amazonaws.com");
            var nullOnlyContext = CreateRequestContext(regionalOnly, new List<DiscoveryEndpointBase> { new TestDiscoveryEndpoint(null) });
            EndpointDiscoveryHandler.DiscoverEndpoints(nullOnlyContext, false, new ImmutableCredentials("AKIDEXAMPLE", "SECRET", null));
            Assert.AreSame(regionalOnly, nullOnlyContext.Request.Endpoint);
        }

        [TestMethod]
        public void ConcurrentRequestsWithDifferentAddressesEachGetTheirOwnEndpoint()
        {
            var addresses = Enumerable.Range(0, 4).Select(i => "https://ingest-cell" + i + ".timestream.us-west-2.amazonaws.com").ToArray();

            Parallel.For(0, 20000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                var address = addresses[i % addresses.Length];
                var uri = EndpointDiscoveryHandler.GetDiscoveredEndpointUri(address);
                if (uri.AbsoluteUri != address + "/" || uri.Host != new Uri(address).Host)
                {
                    throw new InvalidOperationException("Wrong endpoint " + uri + " for " + address);
                }
            });
        }

        private static RequestContext CreateRequestContext(Uri regionalEndpoint, IList<DiscoveryEndpointBase> endpoints)
        {
            var options = new InvokeOptions
            {
                EndpointDiscoveryMarshaller = new DiscoveryMarshaller(),
                EndpointOperation = context => endpoints,
            };
            var originalRequest = new TestRequest();
            return new RequestContext(false, new NullSigner())
            {
                ClientConfig = new MockClientConfig(),
                OriginalRequest = originalRequest,
                Request = new DefaultRequest(originalRequest, "test-service") { Endpoint = regionalEndpoint },
                Options = options,
            };
        }

        private sealed class DiscoveryMarshaller : Amazon.Runtime.Internal.Transform.IMarshaller<EndpointDiscoveryDataBase, AmazonWebServiceRequest>
        {
            public EndpointDiscoveryDataBase Marshall(AmazonWebServiceRequest input) => new TestDiscoveryData(true);
        }
    }
}
