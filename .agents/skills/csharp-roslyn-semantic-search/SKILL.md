---
name: csharp-roslyn-semantic-search
description: Use csharp_roslyn semantic_search when a concept is known but the exact C# symbol name, namespace, or project is not.
---

# Roslyn semantic search

Find source symbols by concept tokens and return stable identities for exact follow-up queries.

## Workflow

1. Form a short concept query from domain terms or likely symbol-name tokens.
2. Call the `csharp_roslyn` MCP tool `semantic_search` with an exact `projectName` when known.
3. Use `symbolKinds` such as `NamedType`, `Method`, or `Property` to reduce noise. Keep `maxResults` at 25 to 50.
4. Review qualified identity, documentation, project, and location; do not select a match by short name alone.
5. Pass the chosen stable ID to `symbol_info`, `symbol_source`, or a relationship tool.

## Common scenarios

- Locate the implementation of a business concept.
- Find likely handlers, services, or policies.
- Discover a symbol before exact navigation.
- Replace lexical filename guessing with compiler symbols.

This is symbol-name and documentation matching, not natural-language proof that code implements a behavior.

