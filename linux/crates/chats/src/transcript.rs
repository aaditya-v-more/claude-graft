//! Reading a Claude Code transcript for what a session record needs.

use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashSet};
use std::path::Path;

/// What a transcript says about the session it holds. A record is what lists
/// a session; the transcript is the session. Everything a sidebar can show
/// comes out of the record, so every field of one has to be pulled out of the
/// other.
///
/// Kept on disk because the sweep writes down the facts behind every record it
/// files, which is how a later pass tells a record still its own from one
/// Claude has rewritten, and because a pass that had to read two hundred
/// megabytes of transcript to learn them should only ever do that once.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SessionFacts {
    #[serde(rename = "cliSessionId")]
    pub cli_session_id: String,
    #[serde(rename = "bridgeIds")]
    pub bridge_ids: Vec<String>,
    #[serde(rename = "ownerAccount")]
    pub owner_account: String,
    #[serde(rename = "ownerOrganization")]
    pub owner_organization: String,
    pub title: String,
    pub cwd: String,
    /// Milliseconds since the epoch, which is what the records carry.
    #[serde(rename = "createdAt")]
    pub created_at: f64,
    #[serde(rename = "lastActivityAt")]
    pub last_activity_at: f64,
    pub model: String,
    pub effort: String,
    #[serde(rename = "permissionMode")]
    pub permission_mode: String,
    /// One per distinct prompt, which is as close to "turns" as a transcript
    /// gets. Claude's own records count only the turns that finished, and
    /// disagree with this by the prompts still in flight; the number is a
    /// badge, not a boundary.
    pub prompts: i64,
    pub branches: Vec<String>,
}

/// What a session asked and what it answered, in the shape `resumed_copies`
/// compares. Kept beside every transcript in the cache, so it holds
/// fingerprints and sizes rather than anything anybody wrote.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
pub struct ConversationTrace {
    /// What a person typed, in order. See `prompts_typed`.
    #[serde(default)]
    pub prompts: Vec<String>,
    /// How much the opening prompt says, whitespace folded. A "hi" opens a
    /// hundred unrelated chats and identifies none of them.
    #[serde(rename = "openingSize", default)]
    pub opening_size: i64,
    /// Every answer that produced something, by its line's own id, with how
    /// much it produced: the bytes of its text, and `EDIT_WEIGHT` for a file it
    /// changed. A resumed copy carries the lines of the history it copied
    /// under their original ids, so what a session did that another did not is
    /// exactly the ids the other lacks.
    #[serde(default)]
    pub work: BTreeMap<String, i64>,
}

impl ConversationTrace {
    /// An edit is work however short its explanation, so one outweighs any
    /// allowance for passing remarks.
    pub const EDIT_WEIGHT: i64 = 1_000_000;
}

const QUOTE: u8 = b'"';
const BACKSLASH: u8 = b'\\';
const COLON: u8 = b':';

/// Hands every `"key":"value"` pair in one line to `take`, in the order they
/// are written.
///
/// One pass rather than a search for each field. A transcript runs to
/// megabytes and there are a dozen fields worth having, so searching it once
/// per field made finding them the slow part of a sweep, measured at twelve
/// seconds over two hundred megabytes against one for this. Bytes rather than
/// characters for the same reason: nothing read here needs to know what a
/// grapheme is.
///
/// Strings are walked the way JSON writes them, backslash escapes and all. A
/// value that ends early takes the rest of the line with it, so a quote pasted
/// into a conversation would otherwise turn its content into keys.
fn pairs<'a>(line: &'a [u8], mut take: impl FnMut(&'a [u8], &'a [u8])) {
    let upper = line.len();
    let closing_quote = |i: usize| -> Option<usize> {
        let mut j = i + 1;
        while j < upper {
            if line[j] == BACKSLASH {
                j += 2;
                continue;
            }
            if line[j] == QUOTE {
                return Some(j);
            }
            j += 1;
        }
        None
    };
    let mut i = 0;
    while i < upper {
        if line[i] != QUOTE {
            i += 1;
            continue;
        }
        let Some(key_end) = closing_quote(i) else { return };
        // A string with a colon after it is a key. Anything else is a value,
        // and what is inside a value is none of this scan's business: skipping
        // it whole is what keeps pasted text out.
        if !(key_end + 2 < upper && line[key_end + 1] == COLON && line[key_end + 2] == QUOTE) {
            i = key_end + 1;
            continue;
        }
        let Some(value_end) = closing_quote(key_end + 2) else { return };
        take(&line[i + 1..key_end], &line[key_end + 3..value_end]);
        i = value_end + 1;
    }
}

/// A pair's value as a string. The escaped case goes back through the JSON
/// reader rather than being unpicked by hand, because a title is the one field
/// here a person writes, and an escaped accent in a sidebar is worse than the
/// cost of parsing six bytes.
fn text(value: &[u8]) -> String {
    if !value.contains(&BACKSLASH) {
        return String::from_utf8_lossy(value).into_owned();
    }
    let mut quoted = Vec::with_capacity(value.len() + 2);
    quoted.push(QUOTE);
    quoted.extend_from_slice(value);
    quoted.push(QUOTE);
    serde_json::from_slice::<String>(&quoted)
        .unwrap_or_else(|_| String::from_utf8_lossy(value).into_owned())
}

/// `"2026-08-29T17:32:48.893Z"` as milliseconds since the epoch. Every stamp
/// transcripts have been seen carrying was fractional, but a whole one is
/// still a stamp.
pub fn epoch_milliseconds(stamp: &str) -> Option<f64> {
    let parsed =
        time::OffsetDateTime::parse(stamp, &time::format_description::well_known::Rfc3339).ok()?;
    Some((parsed.unix_timestamp_nanos() as f64 / 1_000_000.0).round())
}

/// FNV-1a over the words with their whitespace folded. Stable across
/// processes, and the cache outlives every process that writes it.
pub fn prompt_fingerprint(words: &str) -> Option<String> {
    fingerprint(words.as_bytes()).map(|(print, _)| print)
}

/// The same, over a value still as JSON wrote it. Decoding first is what a
/// prompt's words would otherwise cost, and it was measured making a cold
/// pass two thirds slower: a compaction summary or a pasted log is a prompt
/// too. One writer escapes one text one way, so the escaped bytes identify it
/// as well as the words do; only the whitespace escapes are folded, since
/// those are what a re-paste changes.
pub fn prompt_fingerprint_raw(value: &[u8]) -> Option<String> {
    fingerprint(value).map(|(print, _)| print)
}

fn fingerprint(value: &[u8]) -> Option<(String, i64)> {
    const PRIME: u64 = 0x0000_0100_0000_01b3;
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    let (mut wrote, mut gap, mut size) = (false, false, 0i64);
    let mix = |byte: u8, hash: &mut u64, wrote: &mut bool, gap: &mut bool, size: &mut i64| {
        if *gap && *wrote {
            *hash ^= b' ' as u64;
            *hash = hash.wrapping_mul(PRIME);
            *size += 1;
        }
        *gap = false;
        *wrote = true;
        *size += 1;
        *hash ^= byte as u64;
        *hash = hash.wrapping_mul(PRIME);
    };
    let mut i = 0;
    while i < value.len() {
        let byte = value[i];
        if byte == BACKSLASH && i + 1 < value.len() {
            let next = value[i + 1];
            if next == b'n' || next == b't' || next == b'r' {
                gap = true;
            } else {
                mix(byte, &mut hash, &mut wrote, &mut gap, &mut size);
                mix(next, &mut hash, &mut wrote, &mut gap, &mut size);
            }
            i += 2;
            continue;
        }
        if byte == 0x20 || byte == 0x0a || byte == 0x09 || byte == 0x0d {
            gap = true;
        } else {
            mix(byte, &mut hash, &mut wrote, &mut gap, &mut size);
        }
        i += 1;
    }
    wrote.then(|| (format!("{hash:x}"), size))
}

fn contains(haystack: &[u8], needle: &[u8]) -> bool {
    haystack.len() >= needle.len() && haystack.windows(needle.len()).any(|w| w == needle)
}

/// Reads one transcript and pulls out what a record needs. `None` when no
/// bridge line is in it: that is a session the terminal ran on its own, which
/// was never going to be in a sidebar, so nothing is missing.
///
/// The first value a key takes on a line is that line's, which holds for every
/// field read here but one. `type` does not: an answer is written
/// `{"parentUuid":...,"message":{...,"type":"message",...},"type":"assistant"}`,
/// so the first `type` on the line belongs to the message nested inside it and
/// the line's own comes last. A marker line, a bridge or a title, opens with
/// its own `type` and is read by that; an answer is recognised by carrying
/// `assistant` anywhere, which is the only rule the shape above leaves
/// standing.
pub fn session_facts(path: &Path, cli_session_id: &str) -> Option<SessionFacts> {
    read_transcript(path, cli_session_id).0
}

/// What a person typed, one fingerprint per prompt, in the order typed.
///
/// Not the turns: a tool's output comes back as a `user` line too, and so does
/// the note Claude adds about a command that ran, and neither is something
/// anybody said. A prompt is a `user` line with a `promptId` that answers no
/// tool and is not marked `isMeta`, and its words are the message content when
/// that is a string, or its first text block when the prompt came with an
/// image. Whitespace is folded so a prompt pasted again with a trailing
/// newline is still the same prompt.
///
/// Fingerprints rather than the text: these are kept on disk beside every
/// transcript, and a pasted log is no business of a cache.
pub fn prompts_typed(path: &Path) -> Vec<String> {
    let id = path.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
    read_transcript(path, &id).1.prompts
}

pub fn read_transcript(path: &Path, cli_session_id: &str) -> (Option<SessionFacts>, ConversationTrace) {
    let Ok(bytes) = std::fs::read(path) else { return (None, ConversationTrace::default()) };
    read_transcript_bytes(&bytes, cli_session_id)
}

pub fn read_transcript_bytes(bytes: &[u8], cli_session_id: &str) -> (Option<SessionFacts>, ConversationTrace) {
    let mut bridge_ids: Vec<String> = Vec::new();
    let (mut owner_account, mut owner_organization) = (String::new(), String::new());
    let mut title: Option<String> = None;
    let mut first_stamp: Option<String> = None;
    let mut last_stamp: Option<String> = None;
    // A session that got no answer back leaves no line carrying these, so they
    // hold the last values the transcript ever mentioned rather than an honest
    // read; the record still needs something in them. The permission mode is
    // the one a session continued in Claude starts from, so a transcript that
    // names none falls back to the mode that grants least rather than to one
    // nobody chose.
    let mut model = "claude-sonnet-5".to_string();
    let mut effort = "medium".to_string();
    let mut permission_mode = "default".to_string();
    let mut cwd = String::new();
    let mut prompt_ids: HashSet<String> = HashSet::new();
    let mut trace = ConversationTrace::default();
    let mut branches: Vec<String> = Vec::new();

    for line in bytes.split(|b| *b == b'\n') {
        if line.is_empty() {
            continue;
        }
        let mut kind: Option<String> = None;
        let mut assistant = false;
        let (mut stamp, mut line_cwd, mut mode, mut branch) = (None, None, None, None);
        let (mut prompt_id, mut line_model, mut line_effort) = (None, None, None);
        let (mut bridge_id, mut account, mut organization, mut custom_title) = (None, None, None, None);
        let mut content: Option<&[u8]> = None;
        let mut text_block: Option<&[u8]> = None;
        let mut answers_tool = false;
        // An answer's `type` comes last on its line, after the text it
        // carries, so what it produced is counted on every line and only kept
        // once the line turns out to have been an answer.
        let mut line_id: Option<&[u8]> = None;
        let mut produced: i64 = 0;
        let mut edited = false;

        pairs(line, |key, value| {
            let first = |slot: &mut Option<String>| {
                if slot.is_none() {
                    *slot = Some(text(value));
                }
            };
            match key {
                b"type" => {
                    first(&mut kind);
                    if value == b"assistant" {
                        assistant = true;
                    }
                }
                b"timestamp" => first(&mut stamp),
                b"cwd" => first(&mut line_cwd),
                b"permissionMode" => first(&mut mode),
                b"gitBranch" => first(&mut branch),
                b"promptId" => first(&mut prompt_id),
                b"model" => first(&mut line_model),
                b"effort" => first(&mut line_effort),
                b"bridgeSessionId" => first(&mut bridge_id),
                b"ownerAccountUuid" => first(&mut account),
                b"ownerOrganizationUuid" => first(&mut organization),
                b"customTitle" => first(&mut custom_title),
                // Only on a prompt's own line, and only once each: an answer
                // carries megabytes of text in these keys, and decoding it all
                // is what would make a pass slow again.
                b"content" => {
                    if kind.as_deref() == Some("user") && !answers_tool && content.is_none() {
                        content = Some(value);
                    }
                }
                b"text" => {
                    produced += value.len() as i64;
                    if kind.as_deref() == Some("user") && !answers_tool && text_block.is_none() {
                        text_block = Some(value);
                    }
                }
                b"tool_use_id" => answers_tool = true,
                // The line's own id is its last: a tool's input nested in an
                // answer comes before it and may carry one of its own.
                b"uuid" => line_id = Some(value),
                b"name" => {
                    if matches!(value, b"Edit" | b"Write" | b"MultiEdit" | b"NotebookEdit") {
                        edited = true;
                    }
                }
                _ => {}
            }
        });

        if kind.as_deref() == Some("bridge-session") {
            if let Some(id) = bridge_id {
                if !bridge_ids.contains(&id) {
                    bridge_ids.push(id);
                }
            }
            if owner_account.is_empty() {
                owner_account = account.unwrap_or_default();
                owner_organization = organization.unwrap_or_default();
            }
            continue;
        }
        if kind.as_deref() == Some("custom-title") {
            // The last one wins: a session renamed keeps the newer name.
            if custom_title.is_some() {
                title = custom_title;
            }
            continue;
        }
        if let Some(stamp) = stamp {
            if first_stamp.is_none() {
                first_stamp = Some(stamp.clone());
            }
            last_stamp = Some(stamp);
        }
        if assistant {
            if let Some(m) = line_model {
                model = m;
            }
            if let Some(e) = line_effort {
                effort = e;
            }
        }
        if let Some(c) = line_cwd {
            cwd = c;
        }
        if let Some(m) = mode {
            permission_mode = m;
        }
        if let Some(b) = branch {
            if !branches.contains(&b) {
                branches.push(b);
            }
        }
        let had_prompt = prompt_id.is_some();
        if let Some(p) = prompt_id {
            prompt_ids.insert(p);
        }
        if kind.as_deref() == Some("user") && had_prompt && !answers_tool && !contains(line, b"\"isMeta\":true") {
            if let Some(words) = content.or(text_block) {
                if !words.starts_with(b"[Request interrupted by user") {
                    if let Some((print, size)) = fingerprint(words) {
                        if trace.prompts.is_empty() {
                            trace.opening_size = size;
                        }
                        trace.prompts.push(print);
                    }
                }
            }
        }
        // An overloaded or failed request is written as an answer too, and
        // produced nothing anybody would go back for.
        if assistant && (produced > 0 || edited) && !contains(line, b"\"isApiErrorMessage\":true") {
            if let Some((id, _)) = line_id.and_then(fingerprint) {
                *trace.work.entry(id).or_insert(0) +=
                    produced + if edited { ConversationTrace::EDIT_WEIGHT } else { 0 };
            }
        }
    }

    let created = first_stamp.as_deref().and_then(epoch_milliseconds);
    let active = last_stamp.as_deref().and_then(epoch_milliseconds);
    let (Some(created), Some(active)) = (created, active) else { return (None, trace) };
    if bridge_ids.is_empty() || owner_account.is_empty() || owner_organization.is_empty() {
        return (None, trace);
    }

    let facts = SessionFacts {
        cli_session_id: cli_session_id.to_string(),
        bridge_ids,
        owner_account,
        owner_organization,
        title: title.unwrap_or_else(|| "New session".into()),
        cwd,
        created_at: created,
        last_activity_at: active,
        model,
        effort,
        permission_mode,
        prompts: prompt_ids.len() as i64,
        branches,
    };
    (Some(facts), trace)
}
