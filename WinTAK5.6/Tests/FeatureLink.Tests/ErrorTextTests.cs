using System;
using System.Net.Http;
using System.Net.Sockets;
using FeatureLink.Services;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// Asserts the operator-facing error vocabulary.
    ///
    /// These are not cosmetic tests. The message this file governs is, for a field operator, the
    /// entire user interface of a failure — it is all they get and all they can act on. A real
    /// session produced "Could not load layer from URL: Token Required — Token Required" eleven
    /// times in ninety seconds while the operator retried the same URL in five different shapes,
    /// because the text named no cause and suggested no action. The regressions worth guarding
    /// against are therefore: losing the actionable half of a sentence, and letting raw ArcGIS or
    /// socket wording reach a person.
    /// </summary>
    public class ErrorTextTests
    {
        private static ArcGisServiceException Arc(int code, string message = "Token Required") =>
            new ArcGisServiceException(message, code);

        // ── the case that motivated the whole file ──────────────────────────────────

        /// <summary>Signed out: the operator needs to be told to sign in.</summary>
        [Fact]
        public void Token_required_when_signed_out_says_sign_in()
        {
            string text = ErrorText.ForOperator(Arc(499), signedIn: false);

            Assert.Contains("not public", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Sign in", text, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Signed IN: telling them to sign in again is the wrong instruction — they
        /// already did, and the real problem is that the layer is not shared with them. The old
        /// code appended "(sign in again)" to every auth failure regardless.</summary>
        [Fact]
        public void Token_required_when_signed_in_does_not_tell_them_to_sign_in_again()
        {
            string text = ErrorText.ForOperator(Arc(499), signedIn: true);

            Assert.Contains("not shared publicly", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sign in again", text, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(498)]
        [InlineData(499)]
        [InlineData(401)]
        [InlineData(403)]
        public void Every_auth_code_produces_actionable_advice(int code)
        {
            foreach (bool signedIn in new[] { true, false })
            {
                string text = ErrorText.ForOperator(Arc(code, "Token Required"), signedIn);

                Assert.DoesNotContain("Token Required", text, StringComparison.OrdinalIgnoreCase);
                Assert.True(text.Length > 30, $"code {code} signedIn={signedIn} gave a terse message: {text}");
            }
        }

        // ── the doubled-message defect ──────────────────────────────────────────────

        [Theory]
        [InlineData("Token Required — Token Required", "Token Required")]
        [InlineData("Invalid token - Invalid token", "Invalid token")]
        [InlineData("Not found: Not found", "Not found")]
        [InlineData("token required — Token Required", "token required")]
        public void Self_repeating_messages_are_collapsed(string raw, string expected)
        {
            Assert.Equal(expected, ErrorText.Clean(raw));
        }

        /// <summary>A genuinely two-part message must survive — collapsing is only for exact
        /// repetition, never for a reason that happens to contain a separator.</summary>
        [Theory]
        [InlineData("Unable to complete operation — layer not found")]
        [InlineData("Invalid URL: the host could not be resolved")]
        public void Genuinely_two_part_messages_are_preserved(string raw)
        {
            Assert.Equal(raw.TrimEnd('.', ' '), ErrorText.Clean(raw));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Blank_messages_still_say_something(string raw)
        {
            Assert.False(string.IsNullOrWhiteSpace(ErrorText.Clean(raw)));
        }

        // ── service and transport failures ──────────────────────────────────────────

        [Fact]
        public void Not_found_explains_the_expected_url_shape()
        {
            string text = ErrorText.ForOperator(Arc(404, "Not Found"));

            Assert.Contains("FeatureServer", text, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(500)]
        [InlineData(503)]
        [InlineData(504)]
        public void Server_errors_are_attributed_to_the_service_not_the_operator(int code)
        {
            string text = ErrorText.ForOperator(Arc(code, "boom"));

            Assert.Contains("server error", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("try again", text, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A socket failure must be translated, not surfaced as enum wording. The
        /// exception is wrapped the way HttpClient actually delivers it.</summary>
        [Fact]
        public void Dns_failure_is_explained_in_plain_terms()
        {
            var ex = new HttpRequestException("boom", new SocketException((int)SocketError.HostNotFound));

            string text = ErrorText.ForOperator(ex);

            Assert.Contains("could not be found", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SocketException", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Plain_network_failure_mentions_the_connection()
        {
            string text = ErrorText.ForOperator(new HttpRequestException("boom"));

            Assert.Contains("network", text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Cancellation_and_timeout_are_not_reported_as_faults()
        {
            Assert.Contains("cancelled", ErrorText.ForOperator(new OperationCanceledException()),
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("did not respond", ErrorText.ForOperator(new TimeoutException()),
                StringComparison.OrdinalIgnoreCase);
        }

        // ── invariants that must hold for every input ───────────────────────────────

        /// <summary>No operator-facing string may be empty, end mid-sentence, or leak a type
        /// name. This is the backstop for exception types nobody enumerated.</summary>
        [Theory]
        [MemberData(nameof(AllExceptionShapes))]
        public void No_message_is_ever_blank_or_leaks_a_type_name(Exception ex)
        {
            foreach (bool signedIn in new[] { true, false })
            {
                string text = ErrorText.ForOperator(ex, signedIn);

                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
                Assert.DoesNotContain("System.", text, StringComparison.Ordinal);
            }
        }

        public static TheoryData<Exception> AllExceptionShapes => new TheoryData<Exception>
        {
            new ArcGisServiceException("Token Required — Token Required", 499),
            new ArcGisServiceException("Invalid token", 498),
            new ArcGisServiceException("Access denied", 403),
            new ArcGisServiceException("Not Found", 404),
            new ArcGisServiceException("Bad request", 400),
            new ArcGisServiceException("Server blew up", 500),
            new HttpRequestException("boom", new SocketException((int)SocketError.HostNotFound)),
            new HttpRequestException("boom", new SocketException((int)SocketError.ConnectionRefused)),
            new HttpRequestException("boom"),
            new TimeoutException(),
            new OperationCanceledException(),
            new UnauthorizedAccessException("denied"),
            new System.IO.IOException("disk full"),
            new InvalidOperationException("something odd"),
        };

        [Fact]
        public void WithContext_names_the_action_then_the_reason()
        {
            string text = ErrorText.WithContext("Could not add the layer", Arc(499), signedIn: false);

            Assert.StartsWith("Could not add the layer — ", text, StringComparison.Ordinal);
            Assert.Contains("Sign in", text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_null_exception_does_not_throw()
        {
            Assert.False(string.IsNullOrWhiteSpace(ErrorText.ForOperator(null)));
        }
    
        // ── package transfer failures ───────────────────────────────────────────

        /// <summary>The failure seen in the field. The operator was shown only "Failed to send
        /// Data Package"; the log underneath said Commo could not find a usable endpoint for any
        /// destination, which is a connection problem and not a problem with the package.</summary>
        [Fact]
        public void An_endpoint_failure_says_there_is_no_route_and_that_the_package_is_kept()
        {
            string text = ErrorText.ForPackageTransfer("CommoIllegalArgument", null);

            Assert.Contains("no route", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("saved", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CommoIllegalArgument", text, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("TransferFinishedContactGone", "offline")]
        [InlineData("TransferFinishedTimedOut", "timed out")]
        [InlineData("TransferServerUploadFailed", "server")]
        [InlineData("TransferFinishedFileExists", "already has")]
        [InlineData("TransferFinishedDisabledLocally", "disabled")]
        public void Each_known_transfer_status_gets_its_own_explanation(string status, string expected)
        {
            Assert.Contains(expected, ErrorText.ForPackageTransfer(status, null),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>An unknown code is shown rather than explained away, but still paired with
        /// something the operator can do.</summary>
        [Fact]
        public void An_unknown_failure_keeps_the_code_and_still_suggests_an_action()
        {
            string text = ErrorText.ForPackageTransfer("SomethingNew", "with detail");

            Assert.Contains("SomethingNew", text, StringComparison.Ordinal);
            Assert.Contains("saved", text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_empty_failure_still_says_the_package_is_kept()
        {
            foreach (string empty in new[] { null, "", "   " })
                Assert.Contains("saved", ErrorText.ForPackageTransfer(empty, empty),
                    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Never leaves the operator with only a status code and no next step.</summary>
        [Theory]
        [InlineData("CommoIllegalArgument")]
        [InlineData("TransferFinishedFailed")]
        [InlineData("TransferAttemptFailed")]
        [InlineData("")]
        public void Every_transfer_failure_message_tells_the_operator_what_to_do(string status)
        {
            string text = ErrorText.ForPackageTransfer(status, null);

            Assert.True(text.Length > 20, "too terse to act on: " + text);
            Assert.True(
                text.IndexOf("saved", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("try again", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("check", StringComparison.OrdinalIgnoreCase) >= 0,
                "no next step offered: " + text);
        }
}
}
