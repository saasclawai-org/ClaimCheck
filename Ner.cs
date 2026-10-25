using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ClaimCheck;

/// <summary>
/// Local NER tier: a small English NER model (ONNX) that catches free-text
/// names, organizations and locations that regex cannot see. The model is
/// downloaded once on first use and cached under ~/.claimcheck/models/.
/// ON by default (v0.5.0+); set SANITIZE_NER=0 to disable.
/// </summary>
public static class Ner
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("SANITIZE_NER") is not ("0" or "off" or "false");

    public static double Threshold { get; } =
        double.TryParse(Environment.GetEnvironmentVariable("SANITIZE_NER_THRESHOLD"), out var t) ? Math.Clamp(t, 0.05, 0.99) : 0.5;

    public static string ModelRepo { get; } =
        Environment.GetEnvironmentVariable("SANITIZE_NER_MODEL") ?? "Xenova/bert-base-NER";

    private static string ModelDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claimcheck", "models",
        string.Join('_', ModelRepo.Split('/')));

    private const int MaxTokens = 510;

    private static readonly object Gate = new();
    private static InferenceSession? _session;
    private static Dictionary<string, int>? _vocab;
    private static string[]? _id2label;
    private static bool _failed;
    /// <summary>Set when model init fails this run - surfaced via /status and the
    /// status verb so silent NER degradation is visible to the user.</summary>
    public static string? FailReason { get; private set; }
    public static bool Failed => _failed;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    private static readonly Dictionary<string, string> TypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PER"] = "PERSON", ["ORG"] = "ORG", ["LOC"] = "LOC", ["MISC"] = "MISC",
    };

    // Default label order for the dslim/bert-base-NER lineage (Xenova export of it) —
    // verified against the model's config.json. Used only if config.json can't be fetched.
    private static readonly string[] DefaultLabels =
        { "O", "B-MISC", "I-MISC", "B-PER", "I-PER", "B-ORG", "I-ORG", "B-LOC", "I-LOC" };

    private static void EnsureModel()
    {
        lock (Gate)
        {
            if (_session is not null || _failed) return;
            try
            {
                Directory.CreateDirectory(ModelDir);
                var modelPath = Path.Combine(ModelDir, "model.onnx");
                var vocabPath = Path.Combine(ModelDir, "vocab.txt");
                var configPath = Path.Combine(ModelDir, "config.json");

                Download($"{RepoUrl(ModelRepo)}/resolve/main/onnx/model_quantized.onnx", modelPath);
                if (!File.Exists(vocabPath))
                {
                    // prefer the model's own vocab; fall back to the base-cased vocab
                    var alt = $"{RepoUrl(ModelRepo)}/resolve/main/vocab.txt";
                    try { Download(alt, vocabPath); }
                    catch { Download("https://huggingface.co/google-bert/bert-base-cased/resolve/main/vocab.txt", vocabPath); }
                }
                try { Download($"{RepoUrl(ModelRepo)}/resolve/main/config.json", configPath); }
                catch (Exception e) { Console.Error.WriteLine($"claimcheck ner: config.json download failed — falling back to default label order ({e.Message})"); }

                _vocab = File.ReadAllLines(vocabPath)
                    .Select((t, i) => (t, i))
                    .ToDictionary(x => x.t, x => x.i);

                _id2label = DefaultLabels;
                if (File.Exists(configPath))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                        if (doc.RootElement.TryGetProperty("id2label", out var m))
                        {
                            var labels = new string[m.EnumerateObject().Count()];
                            var ok = true;
                            foreach (var p in m.EnumerateObject())
                            {
                                if (!int.TryParse(p.Name, out var idx) || idx < 0 || idx >= labels.Length) { ok = false; break; }
                                labels[idx] = p.Value.GetString() ?? "O";
                            }
                            if (ok && labels.Any(l => l.Contains("PER") || l.Contains("ORG") || l.Contains("LOC")))
                                _id2label = labels;
                        }
                    }
                    catch { }
                }

                _session = new InferenceSession(modelPath);
                Console.Error.WriteLine($"claimcheck ner: ready ({_vocab.Count} vocab, {_id2label.Length} labels)");
                FailReason = null;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"claimcheck ner: model init failed, NER disabled this run — {e.Message}");
                _failed = true;
                FailReason = e.Message;
            }
        }
    }

    private static string RepoUrl(string repo) => $"https://huggingface.co/{repo}";

    private static void Download(string url, string dest)
    {
        if (File.Exists(dest) && new FileInfo(dest).Length > 0) return;
        Console.Error.WriteLine($"claimcheck ner: downloading {Path.GetFileName(dest)} ({url}) …");
        using var resp = Http.Send(new HttpRequestMessage(HttpMethod.Get, url));
        resp.EnsureSuccessStatusCode();
        var tmp = dest + ".tmp";
        using (var src = resp.Content.ReadAsStream())
        using (var dst = File.Create(tmp))
            src.CopyTo(dst);
        File.Move(tmp, dest, true);
    }

    private sealed record Tok(string Token, int Start, int End);

    // BERT basic tokenizer (cased) + WordPiece, with character offsets
    private static List<Tok> Tokenize(string text, Dictionary<string, int> vocab)
    {
        var toks = new List<Tok>();
        foreach (var wordSpan in BasicWords(text))
        {
            var word = text[wordSpan.Start..wordSpan.End];
            if (word.Length > 100) { toks.Add(new Tok("[UNK]", wordSpan.Start, wordSpan.End)); continue; }
            var pieces = WordPieces(word, vocab);
            if (pieces.Count == 0) { toks.Add(new Tok("[UNK]", wordSpan.Start, wordSpan.End)); continue; }
            var pos = wordSpan.Start;
            foreach (var p in pieces)
            {
                var surface = p.StartsWith("##", StringComparison.Ordinal) ? p[2..] : p;
                toks.Add(new Tok(p, pos, pos + surface.Length));
                pos += surface.Length;
            }
        }
        return toks;
    }

    private static IEnumerable<(int Start, int End)> BasicWords(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (start >= 0) { yield return (start, i); start = -1; }
            }
            else if (char.IsPunctuation(c) || char.IsSymbol(c))
            {
                if (start >= 0) { yield return (start, i); start = -1; }
                yield return (i, i + 1);
            }
            else if (start < 0) start = i;
        }
    }

    private static List<string> WordPieces(string word, Dictionary<string, int> vocab)
    {
        var pieces = new List<string>();
        var start = 0;
        while (start < word.Length)
        {
            var end = word.Length;
            string? piece = null;
            while (start < end)
            {
                var sub = word[start..end];
                if (start > 0) sub = "##" + sub;
                if (vocab.ContainsKey(sub)) { piece = sub; break; }
                end--;
            }
            if (piece is null) return new List<string>(); // whole word unknown -> [UNK]
            pieces.Add(piece);
            start = end;
        }
        return pieces;
    }

    private static VocabIds VocabIdsFor(List<Tok> tokens, Dictionary<string, int> vocab)
    {
        var ids = new List<long> { vocab.TryGetValue("[CLS]", out var cls) ? cls : 101 };
        var offsets = new List<(int Start, int End)> { (-1, -1) };
        foreach (var t in tokens)
        {
            ids.Add(vocab.TryGetValue(t.Token, out var id) ? id : vocab.TryGetValue("[UNK]", out var unk) ? unk : 100);
            offsets.Add((t.Start, t.End));
        }
        ids.Add(vocab.TryGetValue("[SEP]", out var sep) ? sep : 102);
        offsets.Add((-1, -1));
        return new VocabIds(ids, offsets);
    }

    private sealed record VocabIds(List<long> Ids, List<(int Start, int End)> Offsets);

    /// <summary>Scrub PERSON/ORG/LOC/MISC entities into stable placeholders.
    /// Returns the scrubbed text and per-entity findings. No-op when NER is disabled.</summary>
    public static (string Text, List<Finding> Findings) Scrub(string text, VaultData vault)
    {
        // NER inference is far too slow for huge inputs (a 1M-token request would grind
        // for minutes on CPU) — above the cap, fall back to the regex tier only.
        var nerMax = 200_000;
        var envMax = Environment.GetEnvironmentVariable("SANITIZE_NER_MAX_CHARS");
        if (int.TryParse(envMax, out var parsed)) nerMax = parsed;
        if (text.Length > nerMax)
        {
            Console.Error.WriteLine($"claimcheck ner: skipped (input {text.Length} chars > {nerMax} cap) — regex tier only");
            return (text, new List<Finding>());
        }
        if (!Enabled) return (text, new List<Finding>());
        EnsureModel();
        if (_session is null || _vocab is null || _id2label is null) return (text, new List<Finding>());

        // protect existing placeholders
        var protectedSpans = new List<(int Start, int End)>();
        foreach (Match m in Regex.Matches(text, @"\[[A-Z][A-Z_0-9]*_\d+\]"))
            protectedSpans.Add((m.Index, m.Index + m.Length));

        var tokens = Tokenize(text, _vocab);
        var entities = new List<(int Start, int End, string Type, double Prob)>();
        var probs = new double[tokens.Count];
        var argmax = new int[tokens.Count];

        for (var offset = 0; offset < tokens.Count; offset += MaxTokens)
        {
            var window = tokens.Skip(offset).Take(MaxTokens).ToList();
            var ids = VocabIdsFor(window, _vocab);
            var len = ids.Ids.Count;
            var mask = ids.Ids.Select(_ => 1L).ToArray();
            var types = new long[len];

            var inputIds = new DenseTensor<long>(new[] { 1, len });
            var attention = new DenseTensor<long>(new[] { 1, len });
            var tokenTypes = new DenseTensor<long>(new[] { 1, len });
            for (var i = 0; i < len; i++) { inputIds[0, i] = ids.Ids[i]; attention[0, i] = mask[i]; tokenTypes[0, i] = types[i]; }

            using var results = _session.Run(new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attention),
                NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypes),
            });
            var logits = results.First(r => r.Name.StartsWith("logits", StringComparison.OrdinalIgnoreCase)).AsTensor<float>();

            var seq = logits.Dimensions[1];
            var nLabels = logits.Dimensions[2];
            for (var pos = 0; pos < seq; pos++)
            {
                double sum = 0, max = double.MinValue; var arg = 0;
                for (var l = 0; l < nLabels; l++)
                {
                    var z = logits[0, pos, l];
                    sum += Math.Exp(z);
                    if (z > max) { max = z; arg = l; }
                }
                var tokIdx = offset + pos - 1; // logits position 0 is [CLS]; 1..window.Count map to our tokens
                if (pos >= 1 && pos <= window.Count && tokIdx >= 0 && tokIdx < tokens.Count)
                { probs[tokIdx] = sum > 0 ? Math.Exp(max - Math.Log(sum)) : 0; argmax[tokIdx] = arg; }
            }
        }

        if (tokens.Count == 0) return (text, new List<Finding>());

        var debug = Environment.GetEnvironmentVariable("SANITIZE_NER_DEBUG") is "1";
        if (debug)
            for (var i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                var surf = t.Start >= 0 && t.End > t.Start ? text[t.Start..t.End] : "<special>";
                Console.Error.WriteLine($"ner tok [{i}] '{t.Token}' -> '{surf}' label='{_id2label[argmax[i]]}' p={probs[i]:F3}");
            }

        // aggregate BIO tags into entity spans
        int? entStart = null, entEnd = null; string? entType = null; var entProbs = new List<double>();
        void Close()
        {
            if (entStart is null || entType is null) return;
            var t = text[entStart.Value..entEnd.Value].Trim();
            var mean = entProbs.Count > 0 ? entProbs.Average() : 0;
            var keep = t.Length >= 2 && entProbs.Count > 0 && mean >= Threshold && !t.All(char.IsDigit);
            if (debug) Console.Error.WriteLine($"ner entity: '{t}' type={entType} mean={mean:F3} -> {(keep ? "KEEP" : "DROP")}");
            if (keep)
                entities.Add((entStart.Value, entEnd.Value, entType, mean));
            entStart = entEnd = null; entType = null; entProbs.Clear();
        }

        for (var i = 0; i < tokens.Count && i < probs.Length; i++)
        {
            var (tok, s, e) = (tokens[i].Token, tokens[i].Start, tokens[i].End);
            if (s < 0 || e <= s) { Close(); continue; } // special tokens
            var label = argmax[i] < _id2label.Length ? _id2label[argmax[i]] : "O";
            var type = label.Length > 2 ? label[2..] : "O"; // B-PER -> PER
            if (!TypeMap.ContainsKey(type)) { Close(); continue; }
            if (label.StartsWith("B-"))
            {
                Close();
                entStart = s; entEnd = e; entType = type; entProbs = new List<double> { probs[i] };
            }
            else if (label.StartsWith("I-") && entStart is not null && type == entType)
            {
                entEnd = e; entProbs.Add(probs[i]);
            }
            else Close();
        }
        Close();

        // Snap entity spans to full word boundaries: WordPiece can split a word
        // ("Doe" -> "Do" + "##e") and a low-confidence trailing subword piece then
        // strands characters outside the span ("[PERSON_1]e") — a partial-PII leak.
        // A detected entity never ends or starts mid-word: extend both edges over
        // adjacent letters/digits in the source text (stop at whitespace/punctuation).
        entities = entities
            .Select(en =>
            {
                var s = en.Start;
                var e = en.End;
                while (e < text.Length && char.IsLetterOrDigit(text[e])) e++;
                while (s > 0 && char.IsLetterOrDigit(text[s - 1])) s--;
                return (Start: s, End: e, Type: en.Type, Prob: en.Prob);
            })
            .OrderBy(en => en.Start)
            .ToList();

        // B-labeled subword pieces fragment one name into several adjacent spans;
        // after snapping they overlap, and overlapping replacements corrupt the
        // text — merge same-type spans that now overlap or touch.
        var mergedEnts = new List<(int Start, int End, string Type, double Prob)>();
        foreach (var en in entities)
        {
            if (mergedEnts.Count > 0)
            {
                var last = mergedEnts[^1];
                if (last.Type == en.Type && en.Start <= last.End)
                {
                    mergedEnts[^1] = (last.Start, Math.Max(last.End, en.End), last.Type, Math.Max(last.Prob, en.Prob));
                    continue;
                }
            }
            mergedEnts.Add(en);
        }
        entities = mergedEnts;

        // drop overlaps with protected placeholders, dedupe, replace right-to-left
        entities = entities
            .Where(en => !protectedSpans.Any(p => en.Start < p.End && en.End > p.Start))
            .GroupBy(en => (en.Start, en.End)).Select(g => g.First())
            .OrderByDescending(en => en.Start).ToList();

        var findings = new List<Finding>();
        foreach (var en in entities)
        {
            var value = text[en.Start..en.End];
            var ph = Sanitizer.Placeholder(vault, TypeMap[en.Type], value);
            var line = 1 + text[..en.Start].Count(c => c == '\n');
            findings.Add(new Finding(line, TypeMap[en.Type], ph));
            text = text[..en.Start] + "[" + ph + "]" + text[en.End..];
        }
        return (text, findings);
    }
}
