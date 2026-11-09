'use strict';
// The storage helper's only script. It runs inside a private copy of Claude's
// own Electron runtime, so the databases are opened by the same Chromium that
// wrote them, and Chromium, rather than a second database writer, owns the
// storage locks and commits the IndexedDB transaction. Everything that
// decides what to write is done by Graft before this starts; this reads the
// sidebar records as they are and writes the ones it is handed.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const READ_KEYS = ['dframe-store', 'LSS-persisted.starred-local-code-sessions', 'LSS-persisted.dframe-local-slice',
    'ccd-sync-owner', 'ccd-sync-active', 'ccd-sync-quarantine', 'ccd-sync-pending:ccd/dframe-store'];

function requireShape(ok) { if (!ok) throw Error('unsupported-storage'); }

// Graft sends its records as JSON with its own key order, so two raw records
// are compared field by field rather than as text.
function same(a, b) {
    return a !== null && b !== null && a.pin === b.pin
        && READ_KEYS.every(key => (a.local[key] ?? null) === (b.local[key] ?? null));
}

// This function runs in an empty, offline document. No Claude application
// scripts, cookies, credentials, extensions or sessions are loaded. It is
// sent into the page as text, so it names its keys itself.
async function storageOperation(action, raw) {
    const names = await indexedDB.databases();
    if (!names.some(db => db.name === 'keyval-store')) throw Error('not-initialized');
    const db = await new Promise((resolve, reject) => {
        const request = indexedDB.open('keyval-store');
        request.onupgradeneeded = () => { request.transaction.abort(); reject(Error('not-initialized')); };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(Error('unreadable-storage'));
    });
    try {
        if (!db.objectStoreNames.contains('keyval')) throw Error('unsupported-storage');
        if (action === 'read') {
            const pin = await new Promise((resolve, reject) => {
                const transaction = db.transaction('keyval', 'readonly');
                const request = transaction.objectStore('keyval').get('store:pin-state:dframe-starred-code');
                request.onsuccess = () => resolve(request.result);
                request.onerror = () => reject(Error('unreadable-storage'));
            });
            if (localStorage.getItem('dframe-store') === null) throw Error('not-initialized');
            if (pin !== undefined && typeof pin !== 'string') throw Error('unsupported-storage');
            return {pin: pin ?? null, local: Object.fromEntries(['dframe-store',
                'LSS-persisted.starred-local-code-sessions', 'LSS-persisted.dframe-local-slice',
                'ccd-sync-owner', 'ccd-sync-active', 'ccd-sync-quarantine', 'ccd-sync-pending:ccd/dframe-store']
                .map(key => [key, localStorage.getItem(key)]))};
        }
        await new Promise((resolve, reject) => {
            const transaction = db.transaction('keyval', 'readwrite', {durability: 'strict'});
            // Restoring a profile that had no record puts it back without one.
            if (raw.pin === null) transaction.objectStore('keyval').delete('store:pin-state:dframe-starred-code');
            else transaction.objectStore('keyval').put(raw.pin, 'store:pin-state:dframe-starred-code');
            transaction.oncomplete = resolve;
            transaction.onerror = transaction.onabort = () => reject(Error('write-failed'));
        });
        for (const key of ['dframe-store', 'LSS-persisted.starred-local-code-sessions',
            'LSS-persisted.dframe-local-slice', 'ccd-sync-pending:ccd/dframe-store']) {
            const value = raw.local[key] ?? null;
            if (value === null) localStorage.removeItem(key);
            else localStorage.setItem(key, value);
        }
        return true;
    } finally { db.close(); }
}

function writeReply(file, data) {
    const temporary = file + '.graft-' + crypto.randomUUID();
    fs.writeFileSync(temporary, JSON.stringify(data), {mode: 0o600, flag: 'wx'});
    fs.renameSync(temporary, file);
}

async function run() {
    const {app, WebContentsView, session, protocol} = require('electron');
    // Chromium leaves its own switches in argv, so the request is the last
    // argument rather than the first.
    const request = JSON.parse(fs.readFileSync(process.argv[process.argv.length - 1], 'utf8'));
    requireShape(['read', 'write'].includes(request.action) && Array.isArray(request.profiles)
        && request.profiles.length > 0 && request.profiles.length <= 32);
    app.setPath('userData', path.join(request.scratch, 'runtime'));
    app.commandLine.appendSwitch('disable-gpu');
    app.commandLine.appendSwitch('disable-background-networking');
    app.commandLine.appendSwitch('disable-component-update');
    app.commandLine.appendSwitch('host-resolver-rules', 'MAP * ~NOTFOUND');
    protocol.registerSchemesAsPrivileged([{scheme: 'app', privileges: {standard: true, secure: true}}]);
    const timeout = setTimeout(() => app.exit(2), 20000);
    await app.whenReady();
    const opened = [];
    const written = [];
    try {
        for (const [index, profile] of request.profiles.entries()) {
            const scratch = path.join(request.scratch, 'profile-' + index);
            fs.mkdirSync(scratch, {recursive: true, mode: 0o700});
            for (const name of ['Local Storage', 'IndexedDB']) {
                const original = path.join(profile.path, name);
                if (!fs.existsSync(original) || !fs.statSync(original).isDirectory()) throw Error('not-initialized');
                fs.symlinkSync(original, path.join(scratch, name));
            }
            const ses = session.fromPath(scratch, {cache: false});
            ses.setPermissionRequestHandler((contents, permission, callback) => callback(false));
            const origins = ['https://claude.ai', 'app://localhost'];
            for (const scheme of ['https', 'http', 'app']) ses.protocol.handle(scheme, req => {
                if (!origins.some(origin => req.url === origin + '/')) return new Response('', {status: 403});
                return new Response('<!doctype html><title>Sidebar storage</title>', {headers: {
                    'Content-Type': 'text/html', 'Content-Security-Policy': "default-src 'none'"}});
            });
            // A view rather than a window: under the headless platform, which
            // keeps this off every display, creating a window crashes Electron.
            const view = new WebContentsView({webPreferences: {
                session: ses, sandbox: true, contextIsolation: true, nodeIntegration: false}});
            const contents = view.webContents;
            const evaluate = (action, raw) => contents.executeJavaScript(
                '(' + storageOperation.toString() + ')(' + JSON.stringify(action) + ',' + JSON.stringify(raw) + ')');
            const item = {profile, contents, ses, evaluate};
            opened.push(item);
            const candidates = [];
            for (const origin of origins) {
                await contents.loadURL(origin + '/');
                try {
                    candidates.push({origin, raw: await evaluate('read', null)});
                } catch (error) {
                    if (!String(error).includes('not-initialized')) throw error;
                }
            }
            requireShape(candidates.length === 1);
            item.origin = candidates[0].origin;
            item.raw = candidates[0].raw;
            await contents.loadURL(item.origin + '/');
            if (request.action === 'write') {
                if (profile.origin !== item.origin || !same(item.raw, profile.expected))
                    throw Error('changed-during-sync');
            }
        }
        if (request.action === 'write') {
            for (const item of opened) {
                try {
                    await item.evaluate('write', item.profile.next);
                    await item.ses.flushStorageData();
                    requireShape(same(await item.evaluate('read', null), item.profile.next));
                    written.push(item.profile.path);
                } catch (error) {
                    await item.evaluate('write', item.raw);
                    await item.ses.flushStorageData();
                    throw error;
                }
            }
        }
        writeReply(request.output, {ok: true, written,
            profiles: opened.map(item => ({path: item.profile.path, origin: item.origin, raw: item.raw}))});
    } catch (error) {
        writeReply(request.output, {ok: false, written, error: String(error.message ?? error).slice(0, 300)});
    } finally {
        for (const item of opened) item.contents.close();
        clearTimeout(timeout);
        app.quit();
    }
}
if (process.versions.electron) run().catch(() => require('electron').app.exit(1));
