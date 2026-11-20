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
using Amazon.Runtime.Internal.Endpoints.StandardLibrary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Text;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins Fn.IsValidHostLabel and Fn.IsVirtualHostableS3Bucket to the behavior of the original
    /// Split-based implementation, which is kept below as a reference.
    /// </summary>
    [TestClass]
    public class EndpointsHostLabelEquivalenceTests
    {
        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        [DataRow("us-west-2", false, true)]
        [DataRow("us-west-2", true, true)]
        [DataRow("a", false, true)]
        [DataRow("", false, false)]
        [DataRow("", true, false)]
        [DataRow(".", true, false)]
        [DataRow("a.", true, false)]
        [DataRow(".a", true, false)]
        [DataRow("a..b", true, false)]
        [DataRow("a.b", false, false)]
        [DataRow("a.b", true, true)]
        [DataRow("-ab", false, false)]
        [DataRow("ab-", false, false)]
        [DataRow("a-b", false, true)]
        [DataRow("a_b", false, false)]
        [DataRow("UPPER", false, true)]
        [DataRow("\u00e9t\u00e9", false, true)]
        [DataRow("123456789012", false, true)]
        public void IsValidHostLabel(string label, bool allowSubDomains, bool expected)
        {
            Assert.AreEqual(expected, Fn.IsValidHostLabel(label, allowSubDomains));
            Assert.AreEqual(expected, ReferenceIsValidHostLabel(label, allowSubDomains));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        [DataRow("my-application-bucket", false, true)]
        [DataRow("my.dotted.bucket", false, false)]
        [DataRow("my.dotted.bucket", true, false)]
        [DataRow("abc.dotted.bucket", true, true)]
        [DataRow("ab", false, false)]
        [DataRow("abc", false, true)]
        [DataRow("Abc", false, false)]
        [DataRow("abC", false, true)] // the last character is not checked for upper case
        [DataRow("aBc", false, false)]
        [DataRow("192.168.0.1", true, false)]
        [DataRow("192.168.0.1", false, false)]
        [DataRow("192.168.0.256", true, false)]
        [DataRow("192.168.0", true, false)]
        [DataRow("192.168.000.001", true, false)]
        [DataRow(" 1.2.3.4", true, false)]
        [DataRow("1.2.3.4.5", true, false)]
        [DataRow("abc.def.ghi.jkl", true, true)]
        [DataRow("abc.de", true, false)]
        [DataRow("abc..def", true, false)]
        [DataRow("abc-", false, false)]
        public void IsVirtualHostableS3Bucket(string bucket, bool allowSubDomains, bool expected)
        {
            Assert.AreEqual(expected, Fn.IsVirtualHostableS3Bucket(bucket, allowSubDomains));
            Assert.AreEqual(expected, ReferenceIsVirtualHostableS3Bucket(bucket, allowSubDomains));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void LengthBoundaries()
        {
            foreach (var length in new[] { 1, 2, 3, 62, 63, 64 })
            {
                var label = new string('a', length);
                foreach (var allow in new[] { false, true })
                {
                    Assert.AreEqual(ReferenceIsValidHostLabel(label, allow), Fn.IsValidHostLabel(label, allow), label);
                    Assert.AreEqual(ReferenceIsVirtualHostableS3Bucket(label, allow), Fn.IsVirtualHostableS3Bucket(label, allow), label);
                    var dotted = label + "." + label;
                    Assert.AreEqual(ReferenceIsValidHostLabel(dotted, allow), Fn.IsValidHostLabel(dotted, allow), dotted);
                    Assert.AreEqual(ReferenceIsVirtualHostableS3Bucket(dotted, allow), Fn.IsVirtualHostableS3Bucket(dotted, allow), dotted);
                }
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void NullInputs()
        {
            Assert.IsFalse(Fn.IsValidHostLabel(null, false));
            Assert.ThrowsExactly<NullReferenceException>(() => Fn.IsValidHostLabel(null, true));
            Assert.ThrowsExactly<NullReferenceException>(() => Fn.IsVirtualHostableS3Bucket(null, false));
            Assert.ThrowsExactly<NullReferenceException>(() => Fn.IsVirtualHostableS3Bucket(null, true));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void RandomCorpusMatchesReference()
        {
            const string alphabet = "ab0-.A9_\u00e9 1+";
            var random = new Random(12345);
            var sb = new StringBuilder();
            for (int i = 0; i < 20000; i++)
            {
                sb.Clear();
                int length = random.Next(0, 20);
                for (int j = 0; j < length; j++)
                {
                    sb.Append(alphabet[random.Next(alphabet.Length)]);
                }
                var value = sb.ToString();
                foreach (var allow in new[] { false, true })
                {
                    Assert.AreEqual(ReferenceIsValidHostLabel(value, allow), Fn.IsValidHostLabel(value, allow), "IsValidHostLabel '" + value + "'");
                    Assert.AreEqual(ReferenceIsVirtualHostableS3Bucket(value, allow), Fn.IsVirtualHostableS3Bucket(value, allow), "IsVirtualHostableS3Bucket '" + value + "'");
                }
            }

            // Dotted-quad shapes, which take the IPv4 parsing path.
            for (int i = 0; i < 5000; i++)
            {
                var value = string.Join(".", random.Next(0, 300), random.Next(0, 300), random.Next(0, 300), random.Next(0, 300));
                foreach (var allow in new[] { false, true })
                {
                    Assert.AreEqual(ReferenceIsVirtualHostableS3Bucket(value, allow), Fn.IsVirtualHostableS3Bucket(value, allow), value);
                }
            }
        }

        // The original implementations, kept verbatim as the reference behavior.
        private static bool ReferenceIsValidHostLabel(string hostLabel, bool allowSubDomains)
        {
            var hosts = new List<string>();
            if (allowSubDomains)
            {
                hosts.AddRange(hostLabel.Split('.'));
            }
            else
            {
                hosts.Add(hostLabel);
            }
            foreach (var host in hosts)
            {
                if (!ReferenceIsVirtualHostableName(host))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ReferenceIsVirtualHostableName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 1 || name.Length > 63)
            {
                return false;
            }
            if (!char.IsLetterOrDigit(name[0]) || !char.IsLetterOrDigit(name[name.Length - 1]))
            {
                return false;
            }
            for (int i = 1; i < name.Length - 1; i++)
            {
                if (!char.IsLetterOrDigit(name[i]) && name[i] != '-')
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ReferenceIsVirtualHostableS3Bucket(string hostLabel, bool allowSubDomains)
        {
            if (ReferenceIsIpV4Address(hostLabel))
            {
                return false;
            }
            var hosts = new List<string>();
            if (allowSubDomains)
            {
                hosts.AddRange(hostLabel.Split('.'));
            }
            else
            {
                hosts.Add(hostLabel);
            }
            foreach (var host in hosts)
            {
                if (!ReferenceIsVirtualHostableS3Name(host))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ReferenceIsVirtualHostableS3Name(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 3 || name.Length > 63)
            {
                return false;
            }
            if (char.IsUpper(name[0]) || !char.IsLetterOrDigit(name[0]) || !char.IsLetterOrDigit(name[name.Length - 1]))
            {
                return false;
            }
            for (int i = 1; i < name.Length - 1; i++)
            {
                if (char.IsUpper(name[i]) || (!char.IsLetterOrDigit(name[i]) && name[i] != '-'))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ReferenceIsIpV4Address(string name)
        {
            var parts = name.Split('.');
            if (parts.Length != 4)
            {
                return false;
            }
            foreach (var part in parts)
            {
                byte result;
                if (!byte.TryParse(part, out result))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
