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
using Amazon.Runtime.Endpoints;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Amazon.Runtime.Internal.Endpoints.StandardLibrary
{
    /// <summary>
    /// Internal implementation of ARN (Amazon Resource Name).
    /// Used by standard library functions to parse and validate ARNs.
    /// </summary>
    public class Arn : PropertyBag
    {
        /// <summary>
        /// ARN partition
        /// </summary>
        public string partition
        {
            get { return (string)this["partition"]; }
            set { this["partition"] = value; }
        }

        /// <summary>
        /// ARN service
        /// </summary>
        public string service
        {
            get { return (string)this["service"]; }
            set { this["service"] = value; }
        }

        /// <summary>
        /// ARN region
        /// </summary>
        public string region
        {
            get { return (string)this["region"]; }
            set { this["region"] = value; }
        }

        /// <summary>
        /// ARN accountId
        /// </summary>
        public string accountId
        {
            get { return (string)this["accountId"]; }
            set { this["accountId"] = value; }
        }

        /// <summary>
        /// ARN list of ResourceIDs
        /// </summary>
        public List<string> resourceId
        {
            get { return (List<string>)this["resourceId"]; }
            set { this["resourceId"] = value; }
        }

        /// <summary>
        /// Check if input string is a valid ARN
        /// </summary>
        public static bool IsArn(string arn)
        {
            return arn != null && arn.StartsWith("arn:");
        }

        /// <summary>
        /// Parses the string into an ARN object.
        /// </summary>
        /// <param name="arnString">String to parse into an ARN.</param>
        /// <param name="arn">The out parameter for the ARN object created by TryParse.</param>
        /// <returns>True if the string was parsed into an ARN object.</returns>
        public static bool TryParse(string arnString, out Arn arn)
        {
            try
            {
                // Parse requires the first token to be exactly "arn", so an input without an ordinal
                // "arn:" prefix can never parse. Rejecting it here skips the culture-sensitive
                // comparison in IsArn for the common case, a plain name such as a bucket or table.
                if (arnString != null && arnString.StartsWith("arn:", StringComparison.Ordinal) && IsArn(arnString))
                {
                    arn = Parse(arnString);
                    return true;
                }
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception) { }
#pragma warning restore CA1031 // Do not catch general exception types
            arn = null;
            return false;
        }

        /// <summary>
        /// Parses the string into an ARN object.
        /// </summary>
        /// <param name="arnString">String to parse into an Arn.</param>
        /// <returns>The Arn object created from the passed in string.</returns>
        /// <exception cref="ArgumentNullException">Thrown if arnString is null.</exception>
        /// <exception cref="ArgumentException">Thrown if the string passed in not valid ARN.</exception>
        public static Arn Parse(string arnString)
        {
            if (arnString == null)
            {
                throw new ArgumentNullException(nameof(arnString));
            }

            const string malformedErrorMessage = "ARN is in incorrect format. ARN format is: arn:<partition>:<service>:<region>:<account-id>:<resource>";

            // Equivalent to arnString.Split(':', 6) without allocating the token array: find the
            // first five colons; whatever follows the fifth is the resource.
            int c0 = arnString.IndexOf(':');
            int c1 = c0 < 0 ? -1 : arnString.IndexOf(':', c0 + 1);
            int c2 = c1 < 0 ? -1 : arnString.IndexOf(':', c1 + 1);
            int c3 = c2 < 0 ? -1 : arnString.IndexOf(':', c2 + 1);
            int c4 = c3 < 0 ? -1 : arnString.IndexOf(':', c3 + 1);
            if (c4 < 0)
            {
                throw new ArgumentException(malformedErrorMessage);
            }

            if (c0 != 3 || string.CompareOrdinal(arnString, 0, "arn", 0, 3) != 0)
            {
                throw new ArgumentException(malformedErrorMessage);
            }

            string partition = arnString.Substring(c0 + 1, c1 - c0 - 1);
            if (string.IsNullOrEmpty(partition))
            {
                throw new ArgumentException("Malformed ARN - no partition specified");
            }
            string service = arnString.Substring(c1 + 1, c2 - c1 - 1);
            if (string.IsNullOrEmpty(service))
            {
                throw new ArgumentException("Malformed ARN - no service specified");
            }

            string region = arnString.Substring(c2 + 1, c3 - c2 - 1);
            string accountId = arnString.Substring(c3 + 1, c4 - c3 - 1);
            string resource = arnString.Substring(c4 + 1);
            if (string.IsNullOrEmpty(resource))
            {
                throw new ArgumentException("Malformed ARN - no resource specified");
            }

            return new Arn
            {
                partition = partition,
                service = service,
                region = region,
                accountId = accountId,
                resourceId = SplitResource(resource)
            };
        }

        /// <summary>
        /// Equivalent to <c>resource.Split(new[] { ':', '/' }).ToList()</c>, including empty entries,
        /// without the intermediate array. The list is created at exactly the size ToList would give it.
        /// </summary>
        private static List<string> SplitResource(string resource)
        {
            int count = 1;
            foreach (var ch in resource)
            {
                if (ch == ':' || ch == '/')
                {
                    count++;
                }
            }

            var result = new List<string>(count);
            int start = 0;
            for (int i = 0; i < resource.Length; i++)
            {
                var ch = resource[i];
                if (ch == ':' || ch == '/')
                {
                    result.Add(resource.Substring(start, i - start));
                    start = i + 1;
                }
            }
            result.Add(resource.Substring(start));
            return result;
        }
    }
}
