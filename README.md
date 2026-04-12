# Vault to Flashcard

Convert your Obsidian vault notes into Anki flashcards using AI.

## Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Anki](https://apps.ankiweb.net/) with [AnkiConnect](https://ankiweb.net/shared/info/2055492159) installed

### API Key

Set your AI API key as a user secret:

```bash
dotnet user-secrets set "ApiKey" "<YOUR_API_KEY>"
```

Supported providers: Gemini, Anthropic, MiniMax, Ollama.

## Usage

```bash
vault-to-flashcard --vault "C:\Path\To\Vault"
```

### Options

| Option                      | Description                                      | Default                |
|-----------------------------|--------------------------------------------------|------------------------|
| `-v\|--vault <PATH>`        | Path to your Obsidian vault                      | Required               |
| `-m\|--model <MODEL>`       | AI model to use                                  | gemini-3-flash-preview |
| `-p\|--provider <PROVIDER>` | AI provider (gemini, anthropic, minimax, ollama) | gemini                 |
| `-c\|--config <PATH>`       | Path to category prompt config JSON              | -                      |
| `--assets <PATH>`           | Custom assets folder path                        | vault/assets/          |
| `--skip-token <TOKEN>`      | Token marking sections to exclude                | SKIP_TOKEN             |
| `--read-only                | Simulate without adding to Anki                  | false                  |

### Examples

Process a vault with Gemini:
```bash
vault-to-flashcard --vault "C:\MyVault"
```

Use a different model:
```bash
vault-to-flashcard --vault "C:\MyVault" --model gemini-3-pro-preview --provider gemini
```

Preview changes without modifying Anki:
```bash
vault-to-flashcard --vault "C:\MyVault" --read-only
```

## How It Works

1.  The script will scan the Obsidian vault you have provided in the `--vault` argument for files with the `study` property. 
    Those with a `true` value (`study: true`) in the YAML frontmatter will be considered for processing, and others will be discarded.
2. Notes that should be studied are split into sections based on markdown headers (`# Header 1`, `## Header 2` `### Header 3`, etc.). Use `%% SKIP_TOKEN %%` in your notes to exclude a section from flashcard generation.
3. The content within each header is parsed into HTML and then fed into your AI API of choice. 
   This will then create atomic flashcards based on the note formats you have provided within the `--config` argument. If a `--config` is not provided, by default it will use the simple "front"/"back" and "cloze" note types.
4. Using AnkiConnect the created cards are then uploaded. Decks are organised hierarchically based on the values provided in the `category` property in YAML frontmatter.
5. Once complete, a `.obsidian-anki-cache.json` file is created in your vault to cache what has been processed by the script.
6. Re-running the script will (un-)suspend cards marked with a different `study` property since the last execution, or re-create cards for header sections that have changed in content since last execution.

## Category Prompt Config

The config file lets you define custom note/card types per category. Each category matches a value from the note's `categories` YAML frontmatter property.

```json
[
  {
    "category": "programming patterns",
    "priority": 1,
    "systemPromptAddendum": "Focus on design patterns, code maintainability, and clean architecture. Use pseudocode for structural examples.",
    "assistantPromptAddendum": "Generate cards using the Pattern Implementation and Trade-off Matrix models. Focus on 'Before vs After' refactoring scenarios.",
    "cardTypes": [
      {
        "modelName": "Basic (Programming)",
        "jsonSchemaProperties": {
          "Front": "What is the front of the card for",
          "Back": "A function that captures its lexical environment"
        },
        "exampleOutput": "{\"Front\": \"What is a closure?\", \"Back\": \"A function that captures its lexical environment.\"}"
      }
    ]
  }
]
```

### Fields

| Field                              | Description                                      |
|------------------------------------|--------------------------------------------------|
| `category`                         | Value from the note's `categories` frontmatter   |
| `priority`                         | Higher priority categories are matched first     |
| `systemPromptAddendum`             | Extra instructions for the AI system prompt      |
| `assistantPromptAddendum`          | Extra instructions appended to the user prompt   |
| `cardTypes[].modelName`            | Name of your Anki note model                     |
| `cardTypes[].jsonSchemaProperties` | Fields your note model requires (name → example) |
| `cardTypes[].exampleOutput`        | JSON example of valid card output                |

Without a config, the tool defaults to simple `Basic` and `Cloze` note types.


