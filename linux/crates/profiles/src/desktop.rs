//! Reading and editing `.desktop` files without disturbing what is in them.
//!
//! A shortcut's file is edited in place, a key at a time, rather than written
//! again from the shortcut: its `Name` may still be the one it was installed
//! under, waiting for the save that renames it, and a menu editor may have
//! added keys of its own that are the person's business.

pub const GROUP: &str = "Desktop Entry";

/// A desktop entry as its lines, so writing it back changes only the keys
/// that were set.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DesktopEntry {
    lines: Vec<String>,
}

impl DesktopEntry {
    pub fn parse(text: &str) -> DesktopEntry {
        DesktopEntry { lines: text.lines().map(str::to_string).collect() }
    }

    pub fn read(path: &std::path::Path) -> Option<DesktopEntry> {
        let bytes = std::fs::read(path).ok()?;
        Some(Self::parse(&String::from_utf8_lossy(&bytes)))
    }

    pub fn text(&self) -> String {
        let mut out = self.lines.join("\n");
        out.push('\n');
        out
    }

    /// The range of lines belonging to the main group, after its header.
    fn group(&self) -> Option<(usize, usize)> {
        let start = self.lines.iter().position(|l| l.trim() == format!("[{GROUP}]"))? + 1;
        let end = self.lines[start..]
            .iter()
            .position(|l| l.trim_start().starts_with('['))
            .map(|n| start + n)
            .unwrap_or(self.lines.len());
        Some((start, end))
    }

    fn find(&self, key: &str) -> Option<usize> {
        let (start, end) = self.group()?;
        (start..end).find(|&i| key_of(&self.lines[i]) == Some(key))
    }

    /// A key's raw value in the main group, before any unescaping. Keys with
    /// a locale (`Name[de]`) are other keys.
    pub fn raw(&self, key: &str) -> Option<&str> {
        let i = self.find(key)?;
        let line = &self.lines[i];
        let (_, value) = line.split_once('=')?;
        Some(value.trim_start())
    }

    /// A string value with the spec's escapes taken out.
    pub fn get(&self, key: &str) -> Option<String> {
        self.raw(key).map(unescape)
    }

    /// Set a key to a raw value, replacing it where it stands or adding it at
    /// the end of the main group. A file with no main group gets one.
    pub fn set_raw(&mut self, key: &str, value: &str) {
        let line = format!("{key}={value}");
        if let Some(i) = self.find(key) {
            self.lines[i] = line;
            return;
        }
        match self.group() {
            Some((start, end)) => {
                // After the group's last line that says anything, so a blank
                // line separating it from the next group stays where it was.
                let mut at = end;
                while at > start && self.lines[at - 1].trim().is_empty() {
                    at -= 1;
                }
                self.lines.insert(at, line);
            }
            None => {
                self.lines.insert(0, format!("[{GROUP}]"));
                self.lines.insert(1, line);
            }
        }
    }

    pub fn set(&mut self, key: &str, value: &str) {
        self.set_raw(key, &escape(value));
    }

    /// Every key in the main group with its raw value, in file order.
    pub fn pairs(&self) -> Vec<(String, String)> {
        let Some((start, end)) = self.group() else { return Vec::new() };
        self.lines[start..end]
            .iter()
            .filter_map(|line| {
                let key = key_of(line)?;
                let (_, value) = line.split_once('=')?;
                Some((key.to_string(), value.trim_start().to_string()))
            })
            .collect()
    }
}

fn key_of(line: &str) -> Option<&str> {
    let trimmed = line.trim_start();
    if trimmed.starts_with('#') {
        return None;
    }
    let (key, _) = trimmed.split_once('=')?;
    Some(key.trim_end())
}

/// The escapes a string value needs. A name is free text, and a newline in it
/// would end the line and turn the rest into a key of its own.
pub fn escape(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    for (i, c) in value.chars().enumerate() {
        match c {
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\t' => out.push_str("\\t"),
            '\r' => out.push_str("\\r"),
            ' ' if i == 0 => out.push_str("\\s"),
            other => out.push(other),
        }
    }
    out
}

pub fn unescape(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    let mut chars = value.chars();
    while let Some(c) = chars.next() {
        if c != '\\' {
            out.push(c);
            continue;
        }
        match chars.next() {
            Some('s') => out.push(' '),
            Some('n') => out.push('\n'),
            Some('t') => out.push('\t'),
            Some('r') => out.push('\r'),
            Some('\\') => out.push('\\'),
            Some(other) => {
                out.push('\\');
                out.push(other);
            }
            None => out.push('\\'),
        }
    }
    out
}

/// One argument of an `Exec` line. The spec quotes an argument holding any
/// reserved character in double quotes, escapes four characters inside them,
/// doubles a percent sign so it is not read as a field code, and then the
/// whole value is a string and escaped again; a home folder with a space in
/// it is the case that makes this matter.
pub fn exec_argument(arg: &str) -> String {
    const RESERVED: &[char] = &[
        ' ', '\t', '\n', '"', '\'', '\\', '>', '<', '~', '|', '&', ';', '$', '*', '?', '#', '(',
        ')', '`',
    ];
    let arg = arg.replace('%', "%%");
    if arg.is_empty() || arg.contains(RESERVED) {
        let mut quoted = String::from("\"");
        for c in arg.chars() {
            if matches!(c, '"' | '`' | '$' | '\\') {
                quoted.push('\\');
            }
            quoted.push(c);
        }
        quoted.push('"');
        quoted
    } else {
        arg
    }
}

/// A raw `Exec` value from its arguments.
pub fn exec_line(args: &[String]) -> String {
    let joined: Vec<String> = args.iter().map(|a| exec_argument(a)).collect();
    // Only the backslash needs the string escape here; the quoting already
    // keeps spaces and newlines out of an argument's way.
    joined.join(" ").replace('\\', "\\\\")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_key_is_replaced_where_it_stands_and_the_rest_is_left_alone() {
        let mut entry = DesktopEntry::parse(
            "[Desktop Entry]\nName=Work\nX-Mine=kept\nExec=old\n\n[Desktop Action new]\nExec=other\n",
        );
        entry.set_raw("Exec", "new");
        entry.set_raw("X-Added", "1");
        let text = entry.text();
        assert!(text.contains("Name=Work\nX-Mine=kept\nExec=new\nX-Added=1\n\n[Desktop Action new]\nExec=other"),
            "only the key set changes, a new one joins the main group, and another group keeps its own Exec: {text}");
    }

    #[test]
    fn a_name_with_a_newline_cannot_add_a_key() {
        let mut entry = DesktopEntry::parse("[Desktop Entry]\n");
        entry.set("Name", "Work\nExec=rm -rf ~");
        assert_eq!(entry.raw("Exec"), None, "the second line of a name is not a key");
        assert_eq!(entry.get("Name").as_deref(), Some("Work\nExec=rm -rf ~"), "and reads back as typed");
    }

    #[test]
    fn an_argument_with_a_space_or_a_percent_is_quoted_for_the_exec_line() {
        let line = exec_line(&["/home/a b/graft-launch".into(), "100%".into()]);
        assert_eq!(line, "\"/home/a b/graft-launch\" 100%%");
        let slash = exec_line(&["/x\\y z".into()]);
        assert_eq!(slash, "\"/x\\\\\\\\y z\"", "a backslash is escaped once for the quotes and once for the string");
    }

    #[test]
    fn a_localised_key_is_not_the_key_itself() {
        let entry = DesktopEntry::parse("[Desktop Entry]\nName[de]=Arbeit\nName=Work\n");
        assert_eq!(entry.get("Name").as_deref(), Some("Work"));
    }
}
