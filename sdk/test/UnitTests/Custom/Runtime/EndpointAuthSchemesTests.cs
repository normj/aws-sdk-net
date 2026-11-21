using Amazon.Runtime;
using Amazon.Runtime.Credentials.Internal;
using Amazon.Runtime.Endpoints;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins BaseAuthResolverHandler.RetrieveSchemesFromEndpoint to its original LINQ implementation (kept below as
    /// Reference): which entries count as schemes, the shared default lists for a single known scheme, the order
    /// and contents of the list for several schemes, and the exceptions for malformed entries.
    /// </summary>
    [TestClass]
    public class EndpointAuthSchemesTests
    {
        private sealed class Probe : BaseAuthResolverHandler
        {
            public static List<IAuthSchemeOption> Retrieve(Endpoint endpoint) => RetrieveSchemesFromEndpoint(endpoint);

            protected override List<IAuthSchemeOption> ResolveAuthOptions(IExecutionContext executionContext) => null;
        }

        // The implementation before the change, verbatim apart from the method name.
        private static List<IAuthSchemeOption> Reference(Endpoint endpoint)
        {
            if (endpoint == null || endpoint.Attributes == null)
            {
                return null;
            }

            var authSchemes = endpoint.Attributes["authSchemes"] as IList;
            if (authSchemes == null)
            {
                return null;
            }

            var schemeNames = authSchemes.OfType<PropertyBag>().Select(scheme => (string)scheme["name"]).ToList();

            if (schemeNames.Count == 1)
            {
                var schemeName = schemeNames.First();
                switch (schemeName)
                {
                    case "sigv4":
                    case "sigv4-s3express":
                        return AuthSchemeOption.DEFAULT_SIGV4;
                    case "sigv4a":
                        return AuthSchemeOption.DEFAULT_SIGV4A;
                    default:
                        break;
                }
            }

            var options = new List<IAuthSchemeOption>();
            foreach (var schemeName in schemeNames)
            {
                switch (schemeName)
                {
                    case "sigv4":
                    case "sigv4-s3express":
                        options.Add(new AuthSchemeOption { SchemeId = AuthSchemeOption.SigV4 });
                        break;
                    case "sigv4a":
                        options.Add(new AuthSchemeOption { SchemeId = AuthSchemeOption.SigV4A });
                        break;
                    default:
                        break;
                }
            }
            return options;
        }

        private static PropertyBag Scheme(object name)
        {
            var bag = new PropertyBag();
            if (name != null)
                bag["name"] = name;
            bag["signingName"] = "s3";
            return bag;
        }

        private static Endpoint WithSchemes(object authSchemes)
        {
            var endpoint = new Endpoint("https://amzn-s3-demo-bucket.s3.us-west-2.amazonaws.com");
            endpoint.Attributes = new PropertyBag();
            if (authSchemes != null)
                endpoint.Attributes["authSchemes"] = authSchemes;
            return endpoint;
        }

        private static void AssertSame(Endpoint endpoint)
        {
            List<IAuthSchemeOption> expected = null, actual = null;
            Exception expectedError = null, actualError = null;
            try { expected = Reference(endpoint); } catch (Exception e) { expectedError = e; }
            try { actual = Probe.Retrieve(endpoint); } catch (Exception e) { actualError = e; }

            if (expectedError != null)
            {
                Assert.IsNotNull(actualError, "expected an exception");
                Assert.AreEqual(expectedError.GetType(), actualError.GetType());
                return;
            }

            Assert.IsNull(actualError, actualError?.ToString());
            if (expected == null)
            {
                Assert.IsNull(actual);
                return;
            }

            Assert.IsNotNull(actual);
            // The shared default lists must be returned as the same instances.
            Assert.AreEqual(ReferenceEquals(expected, AuthSchemeOption.DEFAULT_SIGV4), ReferenceEquals(actual, AuthSchemeOption.DEFAULT_SIGV4));
            Assert.AreEqual(ReferenceEquals(expected, AuthSchemeOption.DEFAULT_SIGV4A), ReferenceEquals(actual, AuthSchemeOption.DEFAULT_SIGV4A));
            CollectionAssert.AreEqual(expected.Select(o => o.SchemeId).ToList(), actual.Select(o => o.SchemeId).ToList());
        }

        private static readonly object[] Names = { "sigv4", "sigv4a", "sigv4-s3express", "SigV4", "unknown", "", null };

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void NoSchemes_MatchesReference()
        {
            AssertSame(null);
            AssertSame(new Endpoint("https://example.com"));
            AssertSame(WithSchemes(null));
            AssertSame(WithSchemes("not a list"));
            AssertSame(WithSchemes(new List<object>()));
            AssertSame(WithSchemes(new List<object> { "sigv4", 5, null }));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void SingleScheme_MatchesReference()
        {
            foreach (var name in Names)
            {
                AssertSame(WithSchemes(new List<object> { Scheme(name) }));
                AssertSame(WithSchemes(new List<object> { "x", Scheme(name), null }));
                AssertSame(WithSchemes(new ArrayList { Scheme(name) }));
                AssertSame(WithSchemes(new object[] { Scheme(name) }));
            }

            Assert.AreSame(AuthSchemeOption.DEFAULT_SIGV4, Probe.Retrieve(WithSchemes(new List<object> { Scheme("sigv4") })));
            Assert.AreSame(AuthSchemeOption.DEFAULT_SIGV4, Probe.Retrieve(WithSchemes(new List<object> { Scheme("sigv4-s3express") })));
            Assert.AreSame(AuthSchemeOption.DEFAULT_SIGV4A, Probe.Retrieve(WithSchemes(new List<object> { Scheme("sigv4a") })));
            Assert.AreEqual(0, Probe.Retrieve(WithSchemes(new List<object> { Scheme("unknown") })).Count);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void SeveralSchemes_MatchReference()
        {
            foreach (var a in Names)
            {
                foreach (var b in Names)
                {
                    AssertSame(WithSchemes(new List<object> { Scheme(a), Scheme(b) }));
                    AssertSame(WithSchemes(new List<object> { Scheme(a), 1, Scheme(b), Scheme("sigv4") }));
                    AssertSame(WithSchemes(new ArrayList { Scheme(a), Scheme(b) }));
                }
            }

            var options = Probe.Retrieve(WithSchemes(new List<object> { Scheme("sigv4a"), Scheme("sigv4") }));
            CollectionAssert.AreEqual(new[] { AuthSchemeOption.SigV4A, AuthSchemeOption.SigV4 }, options.Select(o => o.SchemeId).ToArray());
            Assert.AreNotSame(AuthSchemeOption.DEFAULT_SIGV4, options);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void NonStringName_ThrowsLikeReference()
        {
            AssertSame(WithSchemes(new List<object> { Scheme(42) }));
            AssertSame(WithSchemes(new List<object> { Scheme("sigv4"), Scheme(42) }));
            AssertSame(WithSchemes(new ArrayList { Scheme(true) }));

            Assert.ThrowsExactly<InvalidCastException>(() => Probe.Retrieve(WithSchemes(new List<object> { Scheme(42) })));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void ParsedEndpointAttributes_MatchReference()
        {
            AssertSame(new Endpoint("https://s3.us-west-2.amazonaws.com",
                "{\"authSchemes\":[{\"name\":\"sigv4\",\"signingName\":\"s3\",\"signingRegion\":\"us-west-2\",\"disableDoubleEncoding\":true}]}", null));
            AssertSame(new Endpoint("https://s3.us-west-2.amazonaws.com",
                "{\"authSchemes\":[{\"name\":\"sigv4a\",\"signingRegionSet\":[\"*\"]},{\"name\":\"sigv4\",\"signingRegion\":\"us-west-2\"}]}", null));
            AssertSame(new Endpoint("https://s3.us-west-2.amazonaws.com", "{\"backend\":\"S3\"}", null));
        }
    }
}
