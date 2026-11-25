using System;
using System.Collections.Generic;
using System.Text;

namespace FactionsInstaller.Logic
{
    /// <summary>One key of a Valve text KeyValues file, with the offsets an in-place edit needs.</summary>
    public sealed class VdfNode
    {
        public string Key;
        /// <summary>The unescaped value, or null for a block.</summary>
        public string Value;
        /// <summary>Offset of the value's opening quote, and one past its closing quote.</summary>
        public int ValueStart, ValueEnd;
        public List<VdfNode> Children;
        /// <summary>Offset of the block's closing brace; for the root, the text length.</summary>
        public int CloseBrace;

        public bool IsBlock => Children != null;

        public VdfNode Child(string key)
        {
            if (Children == null) return null;
            foreach (VdfNode c in Children)
                if (string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))
                    return c;
            return null;
        }

        public VdfNode Path(params string[] keys)
        {
            VdfNode n = this;
            foreach (string k in keys)
            {
                n = n.Child(k);
                if (n == null) return null;
            }
            return n;
        }
    }

    /// <summary>
    /// Reads and edits Steam's text VDF (localconfig.vdf, loginusers.vdf, libraryfolders.vdf).
    /// An edit replaces or inserts only the bytes of the one key it targets; everything else in the
    /// file is left exactly as Steam wrote it.
    /// </summary>
    public static class Vdf
    {
        public static VdfNode Parse(string text)
        {
            int pos = 0;
            var root = new VdfNode { Key = "", Children = new List<VdfNode>() };
            ParseBlock(text, ref pos, root, true);
            return root;
        }

        private static void ParseBlock(string text, ref int pos, VdfNode block, bool isRoot)
        {
            while (true)
            {
                SkipTrivia(text, ref pos);
                if (pos >= text.Length)
                {
                    if (!isRoot) throw new FormatException("unterminated block '" + block.Key + "'");
                    block.CloseBrace = text.Length;
                    return;
                }
                char c = text[pos];
                if (c == '}')
                {
                    if (isRoot) throw new FormatException("unexpected '}' at offset " + pos);
                    block.CloseBrace = pos;
                    pos++;
                    return;
                }
                int ks, ke;
                string key = ReadToken(text, ref pos, out ks, out ke);
                SkipTrivia(text, ref pos);
                if (pos >= text.Length) throw new FormatException("key '" + key + "' has no value");
                var node = new VdfNode { Key = key };
                if (text[pos] == '{')
                {
                    pos++;
                    node.Children = new List<VdfNode>();
                    ParseBlock(text, ref pos, node, false);
                }
                else
                {
                    int vs, ve;
                    node.Value = ReadToken(text, ref pos, out vs, out ve);
                    node.ValueStart = vs;
                    node.ValueEnd = ve;
                    SkipConditional(text, ref pos);
                }
                block.Children.Add(node);
            }
        }

        private static void SkipTrivia(string text, ref int pos)
        {
            while (pos < text.Length)
            {
                char c = text[pos];
                if (char.IsWhiteSpace(c)) { pos++; continue; }
                if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
                {
                    while (pos < text.Length && text[pos] != '\n') pos++;
                    continue;
                }
                break;
            }
        }

        private static void SkipConditional(string text, ref int pos)
        {
            int p = pos;
            while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
            if (p < text.Length && text[p] == '[')
            {
                int end = text.IndexOf(']', p);
                pos = end < 0 ? text.Length : end + 1;
            }
        }

        private static string ReadToken(string text, ref int pos, out int start, out int end)
        {
            start = pos;
            if (text[pos] == '"')
            {
                var sb = new StringBuilder();
                pos++;
                while (true)
                {
                    if (pos >= text.Length) throw new FormatException("unterminated string at offset " + start);
                    char c = text[pos];
                    if (c == '"') { pos++; break; }
                    if (c == '\\' && pos + 1 < text.Length)
                    {
                        char n = text[pos + 1];
                        switch (n)
                        {
                            case '\\': sb.Append('\\'); break;
                            case '"': sb.Append('"'); break;
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            default: sb.Append('\\').Append(n); break;
                        }
                        pos += 2;
                        continue;
                    }
                    sb.Append(c);
                    pos++;
                }
                end = pos;
                return sb.ToString();
            }
            if (text[pos] == '{') throw new FormatException("unexpected '{' at offset " + pos);
            while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] != '{' && text[pos] != '}' && text[pos] != '"')
                pos++;
            end = pos;
            return text.Substring(start, end - start);
        }

        public static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// Sets the string at <paramref name="path"/>, creating any missing blocks beneath the
        /// deepest one that exists. The first key of the path must already exist.
        /// </summary>
        public static string SetString(string text, string[] path, string value)
        {
            VdfNode root = Parse(text);
            VdfNode block = root.Child(path[0]);
            if (block == null || !block.IsBlock)
                throw new FormatException("the file has no '" + path[0] + "' block");
            int depth = 1;
            while (depth < path.Length - 1)
            {
                VdfNode next = block.Child(path[depth]);
                if (next == null) break;
                if (!next.IsBlock) throw new FormatException("'" + path[depth] + "' is a value, not a block");
                block = next;
                depth++;
            }
            string quoted = "\"" + Escape(value) + "\"";
            if (depth == path.Length - 1)
            {
                VdfNode existing = block.Child(path[depth]);
                if (existing != null)
                {
                    if (existing.IsBlock) throw new FormatException("'" + path[depth] + "' is a block, not a value");
                    return text.Substring(0, existing.ValueStart) + quoted + text.Substring(existing.ValueEnd);
                }
            }

            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            int lineStart = text.LastIndexOf('\n', Math.Max(0, block.CloseBrace - 1)) + 1;
            string braceIndent = text.Substring(lineStart, block.CloseBrace - lineStart);
            bool braceOnOwnLine = braceIndent.Trim().Length == 0;
            if (!braceOnOwnLine) braceIndent = "";
            string indent = braceIndent + "\t";

            var sb = new StringBuilder();
            for (int i = depth; i < path.Length - 1; i++)
            {
                sb.Append(indent).Append('"').Append(Escape(path[i])).Append('"').Append(nl);
                sb.Append(indent).Append('{').Append(nl);
                indent += "\t";
            }
            sb.Append(indent).Append('"').Append(Escape(path[path.Length - 1])).Append("\"\t\t").Append(quoted).Append(nl);
            for (int i = path.Length - 2; i >= depth; i--)
            {
                indent = indent.Substring(1);
                sb.Append(indent).Append('}').Append(nl);
            }

            int insertAt = braceOnOwnLine ? lineStart : block.CloseBrace;
            string prefix = braceOnOwnLine ? "" : nl;
            return text.Substring(0, insertAt) + prefix + sb + text.Substring(insertAt);
        }
    }
}
