using System.Text;

namespace ParseTiger.Generation;

/// <summary>
/// Builds a conservative, ordered clipboard conversation for web AI clients that
/// reject a single large project-aware prompt.
/// </summary>
public static class ExternalAiMultipartRequest
{
    public const int DefaultContextCharactersPerPart = 24_000;

    public static IReadOnlyList<string> Build(
        string sessionId,
        string snapshotId,
        string solutionPath,
        string projectFilePath,
        string projectName,
        string projectKind,
        string projectFolder,
        string projectContext,
        string requestedChange,
        int contextCharactersPerPart = DefaultContextCharactersPerPart)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        }
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            throw new ArgumentException("A snapshot ID is required.", nameof(snapshotId));
        }
        if (contextCharactersPerPart < 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contextCharactersPerPart),
                "Multipart context chunks must contain at least 1,000 characters.");
        }
        requestedChange = RequestedChange.Require(requestedChange);

        IReadOnlyList<string> chunks =
            SplitContext(projectContext.TrimEnd(), contextCharactersPerPart);
        var parts = new List<string>(chunks.Count);
        for (int index = 0; index < chunks.Count; index++)
        {
            int partNumber = index + 1;
            bool first = index == 0;
            bool final = index == chunks.Count - 1;
            var text = new StringBuilder();
            text.AppendLine("PARSETIGER MULTIPART PROJECT CONTEXT");
            text.AppendLine($"Session: {sessionId}");
            text.AppendLine($"Project snapshot: {snapshotId}");
            text.AppendLine($"Part: {partNumber} of {chunks.Count}");
            text.AppendLine();

            if (first)
            {
                text.AppendLine(
                    "This project-aware request is being delivered in ordered parts " +
                    "because it is too large for one web-chat message.");
                text.AppendLine(
                    "Retain every part in this conversation. Do not analyze the " +
                    "project, propose changes, or produce JSON until the final part.");
                text.AppendLine(
                    "For every non-final part, reply with only the exact acknowledgement " +
                    "shown at the end of that part.");
                text.AppendLine();
                text.AppendLine("PARSETIGER PACKAGE RULES:");
                text.AppendLine(GenerationPrompts.System);
                text.AppendLine();
                text.AppendLine("RESOLVED VISUAL STUDIO TARGET:");
                text.AppendLine("Solution: " + solutionPath);
                text.AppendLine("Project: " + projectName);
                text.AppendLine("Project type: " + projectKind);
                text.AppendLine("Project file: " + projectFilePath);
                text.AppendLine("Project folder: " + projectFolder);
                text.AppendLine(
                    "All operation paths must be relative to this project folder.");
                text.AppendLine(
                    "Do not invent or target files outside this resolved project.");
                text.AppendLine();
            }
            else
            {
                text.AppendLine(
                    "Continue retaining this part after the earlier parts from the " +
                    "same session. Order and snapshot must match.");
                text.AppendLine();
            }

            text.AppendLine(
                $"CURRENT PROJECT FILES — PART {partNumber} OF {chunks.Count}:");
            text.AppendLine(chunks[index]);
            text.AppendLine();

            if (final)
            {
                text.AppendLine("FULL CONTEXT DELIVERED");
                text.AppendLine(
                    $"All {chunks.Count} ordered parts for session {sessionId} and " +
                    $"project snapshot {snapshotId} have now been delivered.");
                text.AppendLine(
                    "Use all retained parts as one continuous CURRENT PROJECT FILES " +
                    "section. The requested change below is the instruction to execute.");
                text.AppendLine(
                    "Now produce the single ParseTiger JSON object required by the " +
                    "package rules. Do not return an acknowledgement, prose, or markdown.");
                text.AppendLine();
                text.AppendLine("REQUESTED CHANGE:");
                text.Append(requestedChange);
            }
            else
            {
                text.Append(
                    $"Reply only: PARSETIGER_CONTEXT_ACK {sessionId} " +
                    $"{partNumber}/{chunks.Count}");
            }

            parts.Add(text.ToString());
        }

        return parts;
    }

    private static IReadOnlyList<string> SplitContext(
        string context,
        int maximumCharacters)
    {
        if (context.Length == 0)
        {
            return [string.Empty];
        }

        var chunks = new List<string>();
        int offset = 0;
        while (offset < context.Length)
        {
            int length = Math.Min(maximumCharacters, context.Length - offset);
            if (offset + length < context.Length)
            {
                int newline = context.LastIndexOf('\n', offset + length - 1, length);
                if (newline >= offset)
                {
                    length = newline - offset + 1;
                }
            }

            chunks.Add(context.Substring(offset, length));
            offset += length;
        }

        return chunks;
    }
}
