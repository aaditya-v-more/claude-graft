//! One connection, read into a buffer and written with backpressure. Every
//! send waits for the bytes to be taken before the next is made, so a slow
//! reader on one side slows the other rather than filling memory.
//!
//! A connection is held as its two halves, because a request still being
//! written is forwarded while its answer comes back, and a spliced socket
//! carries bytes both ways at once.

use crate::http::{Body, ChunkedFraming, Head};
use std::pin::Pin;
use std::task::{Context, Poll};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt, ReadBuf, ReadHalf, WriteHalf};

#[derive(Debug)]
pub enum RelayError {
    Closed,
    TimedOut,
    Truncated,
    Malformed,
    TooLarge,
    Io(std::io::Error),
}

impl std::fmt::Display for RelayError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            RelayError::Closed => f.write_str("the connection closed"),
            RelayError::TimedOut => f.write_str("the connection timed out"),
            RelayError::Truncated => f.write_str("the connection ended part way through"),
            RelayError::Malformed => f.write_str("the message could not be read"),
            RelayError::TooLarge => f.write_str("the message is too large"),
            RelayError::Io(e) => write!(f, "{e}"),
        }
    }
}

impl From<std::io::Error> for RelayError {
    fn from(e: std::io::Error) -> Self {
        RelayError::Io(e)
    }
}

pub type Result<T> = std::result::Result<T, RelayError>;

pub trait Stream: AsyncRead + AsyncWrite + Unpin + Send {}
impl<T: AsyncRead + AsyncWrite + Unpin + Send> Stream for T {}
pub type Boxed = Box<dyn Stream>;

pub struct Reader {
    half: ReadHalf<Boxed>,
    pub buffer: Vec<u8>,
    ended: bool,
    /// Bytes ever received, so a reused connection that dies can be told
    /// apart from one that died partway through an answer.
    pub received: u64,
}

pub struct Writer {
    half: WriteHalf<Boxed>,
}

pub fn split(stream: Boxed) -> (Reader, Writer) {
    split_with(stream, Vec::new())
}

/// The same, for a connection some of whose bytes were read already.
pub fn split_with(stream: Boxed, already: Vec<u8>) -> (Reader, Writer) {
    let (r, w) = tokio::io::split(stream);
    (Reader { half: r, buffer: already, ended: false, received: 0 }, Writer { half: w })
}

impl Reader {
    /// Reads whatever arrives next into the buffer. False once the other side
    /// has finished sending.
    pub async fn fill(&mut self) -> Result<bool> {
        if self.ended {
            return Ok(false);
        }
        if self.buffer.capacity() - self.buffer.len() < 1 << 16 {
            self.buffer.reserve(1 << 16);
        }
        let got = self.half.read_buf(&mut self.buffer).await?;
        if got == 0 {
            self.ended = true;
            return Ok(false);
        }
        self.received += got as u64;
        Ok(true)
    }

    pub fn take(&mut self, count: usize) -> Vec<u8> {
        let count = count.min(self.buffer.len());
        let rest = self.buffer.split_off(count);
        std::mem::replace(&mut self.buffer, rest)
    }

    pub fn take_all(&mut self) -> Vec<u8> {
        std::mem::take(&mut self.buffer)
    }

    /// `None` on a clean end before a single byte of a new head.
    pub async fn read_head(&mut self, limit: usize) -> Result<Option<(Head, Vec<u8>)>> {
        loop {
            if let Some(length) = crate::http::head_length(&self.buffer) {
                let raw = self.take(length);
                let head = Head::parse(&raw).ok_or(RelayError::Malformed)?;
                return Ok(Some((head, raw)));
            }
            if self.buffer.len() > limit {
                return Err(RelayError::TooLarge);
            }
            if !self.fill().await? {
                if self.buffer.is_empty() {
                    return Ok(None);
                }
                return Err(RelayError::Truncated);
            }
        }
    }

    /// A request is read whole, because it may have to be sent twice.
    pub async fn read_body(&mut self, framing: Body, limit: usize) -> Result<Vec<u8>> {
        match framing {
            Body::None => Ok(Vec::new()),
            Body::Length(count) => {
                if count > limit {
                    return Err(RelayError::TooLarge);
                }
                while self.buffer.len() < count {
                    if !self.fill().await? {
                        return Err(RelayError::Truncated);
                    }
                }
                Ok(self.take(count))
            }
            Body::Chunked => {
                let mut framing = ChunkedFraming::default();
                let mut body = Vec::new();
                loop {
                    if !self.buffer.is_empty() {
                        let used = framing.consume(&self.buffer, Some(&mut body)).map_err(|_| RelayError::Malformed)?;
                        self.take(used);
                    }
                    if framing.is_done() {
                        return Ok(body);
                    }
                    if body.len() > limit {
                        return Err(RelayError::TooLarge);
                    }
                    if !self.fill().await? {
                        return Err(RelayError::Truncated);
                    }
                }
            }
            Body::UntilClose => Err(RelayError::Malformed),
        }
    }
}

impl Writer {
    pub async fn send(&mut self, data: &[u8]) -> Result<()> {
        if data.is_empty() {
            return Ok(());
        }
        self.half.write_all(data).await?;
        self.half.flush().await?;
        Ok(())
    }

    /// Says this side has nothing more to send, leaving the other direction
    /// open for whatever is still on its way back.
    pub async fn finish(&mut self) {
        let _ = self.half.shutdown().await;
    }
}

/// Bytes both ways until both sides are done. A tunnel the relay does not
/// open is passed on untouched, and so is a socket an upgrade made.
pub async fn splice(a_r: &mut Reader, a_w: &mut Writer, b_r: &mut Reader, b_w: &mut Writer) {
    let one = pump(a_r, b_w);
    let two = pump(b_r, a_w);
    tokio::pin!(one, two);
    let (mut one_done, mut two_done) = (false, false);
    while !(one_done && two_done) {
        tokio::select! {
            result = &mut one, if !one_done => {
                if result.is_err() { return; }
                one_done = true;
            }
            result = &mut two, if !two_done => {
                if result.is_err() { return; }
                two_done = true;
            }
        }
    }
}

async fn pump(source: &mut Reader, sink: &mut Writer) -> Result<()> {
    if !source.buffer.is_empty() {
        let pending = source.take_all();
        sink.send(&pending).await?;
    }
    while source.fill().await? {
        let pending = source.take_all();
        sink.send(&pending).await?;
    }
    sink.finish().await;
    Ok(())
}

/// A stream whose first bytes were already read off it, handed back in front
/// of the rest: a client may send its TLS greeting straight after `CONNECT`
/// without waiting for the answer.
pub struct Prefixed<S> {
    prefix: Vec<u8>,
    at: usize,
    inner: S,
}

impl<S> Prefixed<S> {
    pub fn new(prefix: Vec<u8>, inner: S) -> Prefixed<S> {
        Prefixed { prefix, at: 0, inner }
    }
}

impl<S: AsyncRead + Unpin> AsyncRead for Prefixed<S> {
    fn poll_read(mut self: Pin<&mut Self>, cx: &mut Context<'_>, buf: &mut ReadBuf<'_>) -> Poll<std::io::Result<()>> {
        if self.at < self.prefix.len() {
            let count = (self.prefix.len() - self.at).min(buf.remaining());
            let at = self.at;
            buf.put_slice(&self.prefix[at..at + count]);
            self.at += count;
            return Poll::Ready(Ok(()));
        }
        Pin::new(&mut self.inner).poll_read(cx, buf)
    }
}

impl<S: AsyncWrite + Unpin> AsyncWrite for Prefixed<S> {
    fn poll_write(mut self: Pin<&mut Self>, cx: &mut Context<'_>, buf: &[u8]) -> Poll<std::io::Result<usize>> {
        Pin::new(&mut self.inner).poll_write(cx, buf)
    }

    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        Pin::new(&mut self.inner).poll_flush(cx)
    }

    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        Pin::new(&mut self.inner).poll_shutdown(cx)
    }
}
