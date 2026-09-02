using System.Text;

namespace Basis.Network.Core
{
    /// <summary>
    /// Client-side utility for normalizing untrusted text before it reaches user-facing UI.
    /// U+FFFD is reserved for the renderer's missing-glyph fallback and is therefore never accepted
    /// as source text by the client. Servers must relay Unicode unchanged and must not call this.
    /// </summary>
    public static class BasisUnicodeSanitizer
    {
        public const char ReplacementCharacter = '\uFFFD';
        public const int ReplacementCodePoint = 0xFFFD;

        /// <summary>
        /// Replaces a source-level U+FFFD with ASCII space. This narrow form is safe for live text
        /// inputs where an IME may transiently expose one half of a surrogate pair while composing.
        /// </summary>
        public static string ReplaceReservedReplacementCharacter(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf(ReplacementCharacter) < 0)
            {
                return text;
            }

            return text.Replace(ReplacementCharacter, ' ');
        }

        /// <summary>
        /// Replaces literal U+FFFD and unpaired UTF-16 surrogates with an ASCII space while
        /// preserving all valid Unicode scalars, including supplementary-plane characters.
        /// Use this on stable text at a client display boundary; live input composition should
        /// use <see cref="ReplaceReservedReplacementCharacter"/> instead.
        /// Clean strings are returned without allocating a replacement string.
        /// </summary>
        public static string SanitizeForDisplay(string text)
        {
            if (string.IsNullOrEmpty(text) || !NeedsSanitization(text))
            {
                return text;
            }

            StringBuilder builder = new StringBuilder(text.Length);
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                if (character == ReplacementCharacter)
                {
                    builder.Append(' ');
                    continue;
                }

                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                    {
                        builder.Append(character);
                        builder.Append(text[++index]);
                    }
                    else
                    {
                        builder.Append(' ');
                    }
                    continue;
                }

                if (char.IsLowSurrogate(character))
                {
                    builder.Append(' ');
                    continue;
                }

                builder.Append(character);
            }

            return builder.ToString();
        }

        private static bool NeedsSanitization(string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                if (character == ReplacementCharacter || char.IsLowSurrogate(character))
                {
                    return true;
                }

                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                    {
                        return true;
                    }
                    index++;
                }
            }

            return false;
        }
    }
}
