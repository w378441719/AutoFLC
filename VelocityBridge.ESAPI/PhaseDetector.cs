using System;

namespace AutoFLC
{
    /// <summary>
    /// Identifies the 0% (end-inspiration) and 50% (end-expiration) phases of a
    /// 4DCT series from the image ID. Kept free of ESAPI types so it can be
    /// unit-tested independently.
    /// </summary>
    public static class PhaseDetector
    {
        /// <summary>
        /// Detect the 4DCT phase from an image ID.
        ///
        /// Only the image ID is trusted: one series can hold several phase
        /// images (e.g. CT_0_1 and CT_50_1 in the same series), so a phase
        /// marker on the series name would mislabel every image in it.
        ///
        /// Only exact "00" or "50" tokens with non-digit boundaries are matched;
        /// keywords such as "INSP"/"EXP" are ignored because they also appear on
        /// non-phase series like Average. When both "00" and "50" are present,
        /// "50" wins (prevents "00" from matching inside values like "500").
        /// </summary>
        /// <param name="seriesId">Series ID (kept for signature compatibility).</param>
        /// <param name="comment">Series comment (kept for signature compatibility).</param>
        /// <param name="imageId">Image ID, which usually embeds a phase marker such as "00" or "50".</param>
        /// <returns>0 for end-inspiration, 50 for end-expiration, -1 if unrecognized.</returns>
        public static int DetectPhase(string seriesId, string comment, string imageId)
        {
            string imgId = Normalize(imageId);
            if (!string.IsNullOrEmpty(imgId))
            {
                if (ContainsToken(imgId, "50"))
                    return 50;
                if (ContainsToken(imgId, "00"))
                    return 0;
            }
            return -1;
        }

        /// <summary>
        /// Checks whether a normalized string contains a token as an isolated
        /// numeric field. "50" matches in "T50", "PHASE50", "50" and "T=50", but
        /// not in "500", "150" or "507"; "00" matches in "00", "T00" and
        /// "PHASE00", but not in "100", "007" or "500".
        /// </summary>
        private static bool ContainsToken(string normalized, string token)
        {
            int idx = 0;
            while ((idx = normalized.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
            {
                int end = idx + token.Length;
                bool leftOk = (idx == 0) || !IsDigit(normalized[idx - 1]);
                bool rightOk = (end == normalized.Length) || !IsDigit(normalized[end]);
                if (leftOk && rightOk)
                    return true;
                idx = end;
            }
            return false;
        }

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.ToUpperInvariant()
                        .Replace("-", "_")
                        .Replace(" ", "_")
                        .Trim();
        }
    }
}
