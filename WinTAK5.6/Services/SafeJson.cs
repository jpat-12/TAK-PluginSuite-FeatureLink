using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FeatureLink.Services
{
    /// <summary>
    /// Bounded JSON parsing for anything that did not originate on this device.
    ///
    /// C-19: <c>JObject.Parse</c> was used directly on a peer-supplied ".featurelinkshare" file
    /// arriving from a Mission Package, with default settings. A deeply nested document causes
    /// stack exhaustion; <c>StackOverflowException</c> is uncatchable on .NET Framework and
    /// terminates the entire WinTAK process — a remote denial of service against the whole
    /// application from a single Mission Package. Newtonsoft 13.0.3 defaults
    /// <c>MaxDepth</c> to 64, which is why the adjudicated severity is HIGH rather than CRITICAL,
    /// but that guarantee is only worth anything once the pinned assembly is actually shipped
    /// inside the .wpk (C-18) — both are fixed, and this pins the limit explicitly so it does not
    /// depend on a library default that a future upgrade could change.
    ///
    /// S10: an explicit <see cref="JsonSerializerSettings"/> with
    /// <c>TypeNameHandling.None</c> is used for every deserialization, because
    /// <c>JsonConvert.DefaultSettings</c> is process-wide and every WinTAK plugin shares one
    /// AppDomain — another plugin enabling <c>TypeNameHandling.All</c> would otherwise turn our
    /// token/config parsing into a remote-code-execution sink.
    /// </summary>
    public static class SafeJson
    {
        /// <summary>Depth cap for untrusted documents. The deepest legitimate FeatureLink share is
        /// ~6 levels (root → sym → vs → entry), so 32 is generous.</summary>
        public const int MaxDepth = 32;

        /// <summary>Byte cap for an untrusted document read from disk or the network. The largest
        /// legitimate share observed is a few KB; 1 MB leaves three orders of magnitude of slack.</summary>
        public const int MaxUntrustedBytes = 1024 * 1024;

        /// <summary>Byte cap for a response from an ArcGIS service we chose to contact. Feature
        /// query pages are legitimately large, so this is much looser than the untrusted cap, but
        /// it is still bounded — an unbounded read of an attacker-influenced URL is not.</summary>
        public const int MaxServiceResponseBytes = 64 * 1024 * 1024;

        public static JsonSerializerSettings StrictSettings => new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = MaxDepth,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
        };

        /// <summary>Parses a JSON object with an explicit depth limit. Throws
        /// <see cref="JsonReaderException"/> for malformed input or a document exceeding
        /// <see cref="MaxDepth"/>, and <see cref="InvalidDataException"/> if the text exceeds
        /// <paramref name="maxBytes"/> (measured as UTF-16 chars, an over-estimate of UTF-8 bytes
        /// only for ASCII — conservative in the right direction).</summary>
        public static JObject ParseObject(string json, int maxBytes = MaxUntrustedBytes)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            if (json.Length > maxBytes)
                throw new InvalidDataException(
                    $"JSON document is {json.Length} characters, over the {maxBytes}-byte limit.");

            using (var stringReader = new StringReader(json))
            using (var reader = new JsonTextReader(stringReader) { MaxDepth = MaxDepth })
            {
                var token = JToken.ReadFrom(reader);
                if (!(token is JObject obj))
                    throw new JsonReaderException("Expected a JSON object at the document root.");
                // Reject trailing content after the object rather than silently ignoring it.
                if (reader.Read())
                    throw new JsonReaderException("Unexpected trailing content after the JSON object.");
                return obj;
            }
        }

        /// <summary>Same as <see cref="ParseObject"/> but returns null instead of throwing —
        /// for persisted-config fragments where "no styling" is the correct degradation.</summary>
        public static JObject ParseObjectOrNull(string json, int maxBytes = MaxUntrustedBytes)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return ParseObject(json, maxBytes);
            }
            catch (Exception ex)
            {
                Log.Warn("Discarding unparseable JSON fragment: " + ex.Message);
                return null;
            }
        }

        /// <summary>Reads a file with a hard size cap applied <b>before</b> the bytes are loaded,
        /// so a 2 GB ".featurelinkshare" cannot be pulled into memory (audit §3.9). Returns null
        /// when the file is over the cap.</summary>
        public static string ReadTextCapped(string path, int maxBytes = MaxUntrustedBytes)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("File not found.", path);
            if (info.Length > maxBytes)
            {
                Log.Warn($"Refusing to read {path}: {info.Length} bytes exceeds the {maxBytes}-byte cap.");
                return null;
            }
            return File.ReadAllText(path);
        }

        public static T Deserialize<T>(string json)
        {
            if (json != null && json.Length > MaxUntrustedBytes)
                throw new InvalidDataException("JSON payload exceeds the size limit.");
            return JsonConvert.DeserializeObject<T>(json, StrictSettings);
        }
    }
}
