//! The certificate Claude Code is told to trust.
//!
//! The relay opens `api.anthropic.com` traffic, so Claude Code has to accept a
//! certificate for that name that Graft made. It is told to through
//! `NODE_EXTRA_CA_CERTS`, which adds to what it trusts and replaces nothing,
//! and the authority it names can vouch for that one host only: a critical
//! name constraint says so, and the authority's private key never reaches the
//! disk at all. It lives in this process for as long as signing the one leaf
//! takes and is dropped, so nothing else can ever be signed by it.

use graft_pool::HOST;
use rcgen::{
    BasicConstraints, CertificateParams, DistinguishedName, DnType, ExtendedKeyUsagePurpose, GeneralSubtree, IsCa,
    KeyPair, KeyUsagePurpose, NameConstraints, SerialNumber,
};
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
use std::os::unix::io::AsRawFd;
use std::path::Path;
use time::{Duration, OffsetDateTime};

/// Made again a couple of months before the leaf runs out, and only by the
/// relay holding `relay.lock`, as it starts. A session started before then
/// goes on trusting the old authority and cannot reach the service through
/// the new relay until it starts again. A relay starts at a login or after an
/// update; this is once in some two years.
pub const RENEWAL: Duration = Duration::days(60);

/// The leaf's life. Kept under the 825 days some TLS stacks enforce, in case
/// anything on the far side ever asks one of them.
pub const LEAF_LIFE: Duration = Duration::days(800);
pub const AUTHORITY_LIFE: Duration = Duration::days(3650);

#[derive(Debug)]
pub struct Failure(pub String);

impl std::fmt::Display for Failure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "The relay's certificate could not be made: {}", self.0)
    }
}

fn fail(stage: &str) -> impl Fn(&dyn std::fmt::Display) -> Failure + '_ {
    move |e| Failure(format!("{stage}: {e}"))
}

pub fn is_present() -> bool {
    [graft_pool::certificate_file(), graft_pool::identity_file(), graft_pool::leaf_file()].iter().all(|p| p.exists())
}

/// When the leaf on disk stops being valid, or `None` when it will not read.
pub fn leaf_expiry(path: &Path) -> Option<i64> {
    let text = fs::read(path).ok()?;
    let (_, pem) = x509_parser::pem::parse_x509_pem(&text).ok()?;
    let certificate = pem.parse_x509().ok()?;
    Some(certificate.validity().not_after.timestamp())
}

fn random_serial() -> SerialNumber {
    let mut bytes = [0u8; 16];
    // SAFETY: fills the buffer it is given.
    unsafe {
        libc::getrandom(bytes.as_mut_ptr().cast(), bytes.len(), 0);
    }
    // Positive, as a serial must be.
    bytes[0] &= 0x7f;
    SerialNumber::from_slice(&bytes)
}

/// Serialised against two relays doing it at once with a lock file, so the
/// authority Claude Code is told to trust is always the one that signed the
/// certificate the relay presents.
pub fn ensure() -> Result<(), Failure> {
    let directory = graft_pool::directory();
    fs::create_dir_all(&directory).map_err(|e| fail("folder")(&e))?;
    let _ = fs::set_permissions(&directory, fs::Permissions::from_mode(0o700));
    let lock = OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .mode(0o600)
        .open(directory.join(".lock"))
        .map_err(|e| fail("lock")(&e))?;
    // SAFETY: flock on a descriptor this function owns; let go when `lock`
    // is dropped and closed.
    unsafe {
        libc::flock(lock.as_raw_fd(), libc::LOCK_EX);
    }

    let now = OffsetDateTime::now_utc();
    if is_present() && leaf_expiry(&graft_pool::leaf_file()).is_some_and(|t| t > (now + RENEWAL).unix_timestamp()) {
        return Ok(());
    }

    let authority_key = KeyPair::generate().map_err(|e| fail("authority key")(&e))?;
    let mut authority = CertificateParams::new(Vec::<String>::new()).map_err(|e| fail("authority")(&e))?;
    let mut name = DistinguishedName::new();
    name.push(DnType::CommonName, format!("Claude Graft relay for {HOST}"));
    authority.distinguished_name = name;
    authority.is_ca = IsCa::Ca(BasicConstraints::Constrained(0));
    authority.key_usages = vec![KeyUsagePurpose::KeyCertSign, KeyUsagePurpose::CrlSign];
    authority.name_constraints = Some(NameConstraints {
        permitted_subtrees: vec![GeneralSubtree::DnsName(HOST.to_string())],
        excluded_subtrees: vec![],
    });
    authority.not_before = now - Duration::minutes(5);
    authority.not_after = now + AUTHORITY_LIFE;
    authority.serial_number = Some(random_serial());
    let authority_certificate = authority.self_signed(&authority_key).map_err(|e| fail("authority")(&e))?;

    let leaf_key = KeyPair::generate().map_err(|e| fail("leaf key")(&e))?;
    let mut leaf = CertificateParams::new(vec![HOST.to_string()]).map_err(|e| fail("leaf")(&e))?;
    let mut name = DistinguishedName::new();
    name.push(DnType::CommonName, HOST);
    leaf.distinguished_name = name;
    leaf.is_ca = IsCa::ExplicitNoCa;
    leaf.key_usages = vec![KeyUsagePurpose::DigitalSignature, KeyUsagePurpose::KeyEncipherment];
    leaf.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
    leaf.use_authority_key_identifier_extension = true;
    leaf.not_before = now - Duration::minutes(5);
    leaf.not_after = now + LEAF_LIFE;
    leaf.serial_number = Some(random_serial());
    let leaf_certificate =
        leaf.signed_by(&leaf_key, &authority_certificate, &authority_key).map_err(|e| fail("leaf")(&e))?;
    // The authority's key goes here, having signed the one certificate it
    // ever will.
    drop(authority_key);

    let work = directory.join(format!(".new-{}", uuid::Uuid::new_v4().simple()));
    fs::create_dir(&work).map_err(|e| fail("work folder")(&e))?;
    let _ = fs::set_permissions(&work, fs::Permissions::from_mode(0o700));
    let written = (|| -> Result<(), Failure> {
        let put = |name: &str, text: &str| -> Result<(), Failure> {
            let mut file = OpenOptions::new()
                .write(true)
                .create_new(true)
                .mode(0o600)
                .open(work.join(name))
                .map_err(|e| fail(name)(&e))?;
            file.write_all(text.as_bytes()).and_then(|_| file.sync_all()).map_err(|e| fail(name)(&e))
        };
        put("identity.pem", &leaf_key.serialize_pem())?;
        put("leaf.pem", &leaf_certificate.pem())?;
        put("ca.pem", &authority_certificate.pem())?;
        // Readable by Claude Code, which runs as the same person; the folder
        // around it is what keeps everybody else out.
        let _ = fs::set_permissions(work.join("ca.pem"), fs::Permissions::from_mode(0o644));
        // The authority last: until it lands, a reader sees the old pair or
        // none, never a new authority beside an old certificate.
        for (name, target) in [
            ("identity.pem", graft_pool::identity_file()),
            ("leaf.pem", graft_pool::leaf_file()),
            ("ca.pem", graft_pool::certificate_file()),
        ] {
            fs::rename(work.join(name), &target).map_err(|e| fail(name)(&e))?;
        }
        Ok(())
    })();
    let _ = fs::remove_dir_all(&work);
    written
}

/// The blocks of a PEM file, each with its label and the bytes it holds.
pub fn pem_blocks(text: &str) -> Vec<(String, Vec<u8>)> {
    use base64::Engine;
    let mut out = Vec::new();
    let mut label: Option<String> = None;
    let mut body = String::new();
    for line in text.lines() {
        let line = line.trim();
        if let Some(rest) = line.strip_prefix("-----BEGIN ").and_then(|r| r.strip_suffix("-----")) {
            label = Some(rest.to_string());
            body.clear();
        } else if line.starts_with("-----END ") {
            if let Some(name) = label.take() {
                if let Ok(der) = base64::engine::general_purpose::STANDARD.decode(body.as_bytes()) {
                    out.push((name, der));
                }
            }
        } else if label.is_some() {
            body.push_str(line);
        }
    }
    out
}

/// The leaf and its authority, and the leaf's key, as the TLS listener takes
/// them.
pub fn identity() -> Result<
    (Vec<rustls::pki_types::CertificateDer<'static>>, rustls::pki_types::PrivateKeyDer<'static>),
    Failure,
> {
    let read = |path: &Path| fs::read_to_string(path).map_err(|e| fail("identity")(&e));
    let mut chain = Vec::new();
    for path in [graft_pool::leaf_file(), graft_pool::certificate_file()] {
        for (label, der) in pem_blocks(&read(&path)?) {
            if label == "CERTIFICATE" {
                chain.push(rustls::pki_types::CertificateDer::from(der));
            }
        }
    }
    let key = pem_blocks(&read(&graft_pool::identity_file())?)
        .into_iter()
        .find(|(label, _)| label == "PRIVATE KEY")
        .map(|(_, der)| rustls::pki_types::PrivateKeyDer::Pkcs8(der.into()))
        .ok_or_else(|| Failure("identity: no key".into()))?;
    if chain.is_empty() {
        return Err(Failure("identity: no certificate".into()));
    }
    Ok((chain, key))
}

#[cfg(test)]
mod tests {
    use super::*;
    use graft_core::testing::Sandbox;
    use x509_parser::extensions::{GeneralName, ParsedExtension};

    fn parsed(path: &Path) -> Vec<u8> {
        let text = fs::read(path).unwrap();
        let (_, pem) = x509_parser::pem::parse_x509_pem(&text).unwrap();
        pem.contents
    }

    #[test]
    fn the_authority_claude_code_trusts_can_vouch_for_the_services_name_alone() {
        let _sandbox = Sandbox::new();
        ensure().unwrap();
        let der = parsed(&graft_pool::certificate_file());
        let (_, authority) = x509_parser::parse_x509_certificate(&der).unwrap();
        let constraint = authority
            .extensions()
            .iter()
            .find(|e| matches!(e.parsed_extension(), ParsedExtension::NameConstraints(_)))
            .expect("a name constraint");
        assert!(constraint.critical, "a critical one, which a verifier may not skip");
        let ParsedExtension::NameConstraints(nc) = constraint.parsed_extension() else { unreachable!() };
        let permitted: Vec<String> = nc
            .permitted_subtrees
            .as_ref()
            .unwrap()
            .iter()
            .map(|s| match &s.base {
                GeneralName::DNSName(name) => name.to_string(),
                other => format!("{other:?}"),
            })
            .collect();
        assert_eq!(permitted, [HOST]);
        let basic = authority.basic_constraints().unwrap().unwrap();
        assert!(basic.critical && basic.value.ca && basic.value.path_len_constraint == Some(0));
    }

    #[test]
    fn the_certificate_the_relay_presents_is_signed_by_that_authority() {
        let _sandbox = Sandbox::new();
        ensure().unwrap();
        let authority_der = parsed(&graft_pool::certificate_file());
        let leaf_der = parsed(&graft_pool::leaf_file());
        let (_, authority) = x509_parser::parse_x509_certificate(&authority_der).unwrap();
        let (_, leaf) = x509_parser::parse_x509_certificate(&leaf_der).unwrap();
        assert!(leaf.verify_signature(Some(authority.public_key())).is_ok());
        let names: Vec<String> = leaf
            .subject_alternative_name()
            .unwrap()
            .unwrap()
            .value
            .general_names
            .iter()
            .map(|n| match n {
                GeneralName::DNSName(name) => name.to_string(),
                other => format!("{other:?}"),
            })
            .collect();
        assert_eq!(names, [HOST]);
    }

    #[test]
    fn no_private_key_but_the_leafs_is_left_on_disk_and_that_one_is_this_users_alone() {
        let _sandbox = Sandbox::new();
        ensure().unwrap();
        let names: Vec<String> =
            fs::read_dir(graft_pool::directory()).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect();
        assert!(!names.iter().any(|n| n.ends_with(".key") || n.starts_with(".new-")));
        let keys: usize = names
            .iter()
            .map(|n| pem_blocks(&fs::read_to_string(graft_pool::directory().join(n)).unwrap_or_default()))
            .map(|blocks| blocks.iter().filter(|(l, _)| l.contains("PRIVATE KEY")).count())
            .sum();
        assert_eq!(keys, 1);
        let mode = fs::metadata(graft_pool::identity_file()).unwrap().permissions().mode() & 0o777;
        assert_eq!(mode, 0o600);
        assert!(identity().is_ok(), "and the listener can take it up");
    }

    #[test]
    fn a_certificate_far_from_its_end_is_kept_as_it_is() {
        let _sandbox = Sandbox::new();
        ensure().unwrap();
        let before = fs::read(graft_pool::certificate_file()).unwrap();
        ensure().unwrap();
        assert_eq!(fs::read(graft_pool::certificate_file()).unwrap(), before);
        fs::remove_file(graft_pool::leaf_file()).unwrap();
        ensure().unwrap();
        assert_ne!(fs::read(graft_pool::certificate_file()).unwrap(), before, "while one missing its leaf is made again whole");
    }
}
