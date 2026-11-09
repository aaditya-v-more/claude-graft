//! The addresses this app sends people to.
//!
//! One account name, spelled once. The site and the source both hang off it,
//! and a build already installed keeps whatever it was compiled with, so a
//! rename that touched one of the two would leave a dead link in every copy.
//!
//! The tip jar is a second account somewhere else entirely, and it is spelled
//! differently. GitHub Sponsors cannot pay into an Indian account, so the money
//! goes through Ko-fi, whose handle carries no hyphens: nothing may derive one
//! name from the other.

pub const OWNER: &str = "aaditya-v-more";
pub const KOFI_ACCOUNT: &str = "aadityavmore";

/// The developer, as a person rather than a repository. A third and fourth
/// spelling, for the same reason as the tip jar: neither is the GitHub name.
pub const EMAIL: &str = "aadityavmore@gmail.com";
pub const LINKEDIN: &str = "https://www.linkedin.com/in/aadityavmore/";

pub fn site() -> String {
    format!("https://{OWNER}.github.io/claude-graft/")
}

pub fn source() -> String {
    format!("https://github.com/{OWNER}/claude-graft")
}

pub fn support() -> String {
    format!("https://ko-fi.com/{KOFI_ACCOUNT}")
}

pub fn releases() -> String {
    format!("https://github.com/{OWNER}/claude-graft/releases")
}

pub fn new_issue() -> String {
    format!("https://github.com/{OWNER}/claude-graft/issues/new")
}

/// A message that already says which Graft and which system it comes from,
/// because the first reply to a problem report is otherwise always a question
/// asking for exactly that.
pub fn mail(version: &str, system: &str) -> String {
    let subject = format!("Claude Graft {version}");
    let body = format!("\n\n--\nClaude Graft {version}, {system}");
    format!("mailto:{EMAIL}?subject={}&body={}", escape(&subject), escape(&body))
}

fn escape(text: &str) -> String {
    let mut out = String::new();
    for byte in text.bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'.' | b'_' | b'~' => out.push(byte as char),
            _ => out.push_str(&format!("%{byte:02X}")),
        }
    }
    out
}

/// The host's own name for itself. Inside the Flatpak `/etc/os-release` is the
/// runtime's, which would have every report say it came from GNOME's SDK, so
/// the host's copy Flatpak exposes is read first.
pub fn system_name() -> String {
    for path in ["/run/host/os-release", "/etc/os-release"] {
        if let Ok(text) = std::fs::read_to_string(path) {
            if let Some(name) = pretty_name(&text) {
                return name;
            }
        }
    }
    "Linux".into()
}

pub fn pretty_name(os_release: &str) -> Option<String> {
    os_release.lines().find_map(|line| {
        let value = line.strip_prefix("PRETTY_NAME=")?;
        let value = value.trim().trim_matches('"').trim_matches('\'');
        (!value.is_empty()).then(|| value.to_string())
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_address_hangs_off_the_names_spelled_once() {
        assert_eq!(site(), "https://aaditya-v-more.github.io/claude-graft/");
        assert_eq!(source(), "https://github.com/aaditya-v-more/claude-graft");
        assert_eq!(support(), "https://ko-fi.com/aadityavmore", "the tip jar keeps its own spelling");
        assert!(EMAIL.starts_with("aadityavmore@") && LINKEDIN.contains("/in/aadityavmore/"),
            "the developer's address and profile are spelled out, not derived from the repository's name");
    }

    #[test]
    fn a_problem_report_already_says_which_graft_and_which_system() {
        let mail = mail("2.0.1", "Fedora Linux 44");
        assert!(mail.starts_with("mailto:aadityavmore@gmail.com?subject=Claude%20Graft%202.0.1"));
        assert!(mail.contains("Fedora%20Linux%2044"), "the body ends in the system it came from");
        assert_eq!(pretty_name("NAME=Fedora\nPRETTY_NAME=\"Bazzite 44\"\n").as_deref(), Some("Bazzite 44"));
    }
}
