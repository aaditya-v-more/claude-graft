import Foundation

/// The launcher carries this with it so a shortcut does not depend on Graft
/// still being installed. Chromium, rather than a second database writer,
/// owns the storage locks and commits the IndexedDB transaction.
enum SidebarStorage {
    static let script = #"""
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const FRAME = 'dframe-store';
const PINS = 'store:pin-state:dframe-starred-code';
const LEGACY_PINS = 'LSS-persisted.starred-local-code-sessions';
const SLICE = 'LSS-persisted.dframe-local-slice';
const OWNER = 'ccd-sync-owner';
const ACTIVE = 'ccd-sync-active';
const QUARANTINE = 'ccd-sync-quarantine';
const PENDING = 'ccd-sync-pending:ccd/dframe-store';
const unique = values => [...new Set(values)];
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const strings = value => Array.isArray(value) && value.every(item => typeof item === 'string');
const localID = value => /^local_[a-zA-Z0-9-]+$/.test(value);
const digest = value => crypto.createHash('sha256').update(JSON.stringify(value)).digest('hex');
function requireShape(ok) { if (!ok) throw Error('unsupported-storage'); }
function parse(value) { try { return JSON.parse(value); } catch { throw Error('unreadable-storage'); } }

// Unrelated entries retain their positions; only slots belonging to shared
// Code sessions can be replaced or removed.
function replaceShared(existing, shared, wanted) {
    let next = 0;
    const result = [];
    for (const item of existing) {
        if (!shared.has(item)) result.push(item);
        else if (next < wanted.length) result.push(wanted[next++]);
    }
    return unique(result.concat(wanted.slice(next)));
}

function decode(raw) {
    const frame = parse(raw.local[FRAME]);
    const pin = parse(raw.pin);
    requireShape(object(frame) && frame.version === 1 && object(frame.state)
        && strings(frame.state.pinnedOrder)
        && object(frame.state.sortByByMode)
        && object(pin) && pin.version === 0 && object(pin.state)
        && strings(pin.state.starredIds));
    const sort = frame.state.sortByByMode.code ?? 'recency';
    requireShape(['recency', 'alpha', 'created'].includes(sort));
    for (const key of [LEGACY_PINS, SLICE]) {
        if (raw.local[key] === null) continue;
        const value = parse(raw.local[key]);
        requireShape(object(value) && (key === LEGACY_PINS ? strings(value.value)
            : object(value.value) && strings(value.value.pinnedOrder)));
    }
    return {frame, pin, sort};
}

function snapshot(raw) {
    const {frame, pin, sort} = decode(raw);
    const pins = unique(pin.state.starredIds.filter(localID));
    const pinned = new Set(pins);
    const order = unique(frame.state.pinnedOrder.filter(id => id.startsWith('code:'))
        .map(id => id.slice(5)).filter(id => pinned.has(id)).concat(pins));
    const slice = raw.local[SLICE] === null ? null : parse(raw.local[SLICE]);
    return {pins: pins.sort(), order, sort,
        orderTime: typeof slice?.timestamp === 'number' ? slice.timestamp : 0,
        scope: frame.state.lastSidebarScopeKey ?? null, fingerprint: digest(raw)};
}

function patch(raw, change) {
    const {frame, pin, sort} = decode(raw);
    requireShape(strings(change.shared) && change.shared.every(localID)
        && strings(change.pins) && strings(change.order)
        && change.pins.every(id => change.shared.includes(id))
        && change.order.length === new Set(change.order).size
        && change.order.length === change.pins.length
        && change.order.every(id => change.pins.includes(id))
        && ['recency', 'alpha', 'created'].includes(change.sort));
    const shared = new Set(change.shared);
    const sharedKeys = new Set(change.shared.map(id => 'code:' + id));
    const orderKeys = change.order.map(id => 'code:' + id);
    const now = Math.max(Date.now(), (pin.updatedAt ?? 0) + 1);
    pin.state.starredIds = replaceShared(pin.state.starredIds, shared, change.order);
    pin.updatedAt = now;
    frame.state.pinnedOrder = replaceShared(frame.state.pinnedOrder, sharedKeys, orderKeys);
    frame.state.sortByByMode.code = change.sort;
    const local = {...raw.local, [FRAME]: JSON.stringify(frame)};
    if (sort !== change.sort) {
        // Claude reconciles this store with its account settings on startup.
        // Use its own account-scoped pending-edit marker, otherwise the server
        // immediately restores the old sort choice. Pins are local-only and
        // must not create an account preference upload by themselves.
        const scope = frame.state.lastSidebarScopeKey;
        const owner = raw.local[OWNER];
        requireShape(raw.local[QUARANTINE] !== '1');
        if (owner !== null) {
            requireShape(typeof scope === 'string' && scope.split('/').length === 2
                && owner === scope.split('/')[0]);
            const pending = raw.local[PENDING];
            requireShape(pending === null || pending === '1' || pending === scope
                || pending === scope + '|migrate');
            local[PENDING] = scope;
        } else {
            requireShape(raw.local[ACTIVE] !== '1' && raw.local[PENDING] === null);
        }
    }
    const legacy = raw.local[LEGACY_PINS] === null ? {value: [], tabId: 'graft', timestamp: 0}
        : parse(raw.local[LEGACY_PINS]);
    legacy.value = replaceShared(legacy.value, shared, change.order);
    legacy.timestamp = Math.max(now, (legacy.timestamp ?? 0) + 1);
    local[LEGACY_PINS] = JSON.stringify(legacy);
    const slice = raw.local[SLICE] === null
        ? {value: {pinnedOrder: [], homeProjectsPinnedOrder: []}, tabId: 'graft', timestamp: 0}
        : parse(raw.local[SLICE]);
    slice.value.pinnedOrder = replaceShared(slice.value.pinnedOrder, sharedKeys, orderKeys);
    slice.timestamp = Math.max(now, (slice.timestamp ?? 0) + 1);
    local[SLICE] = JSON.stringify(slice);
    return {...raw, local, pin: JSON.stringify(pin)};
}

function patchPreferences(data, change) {
    const result = structuredClone(data);
    requireShape(object(result) && (result.preferences === undefined || object(result.preferences)));
    const prefs = result.preferences ??= {};
    requireShape(prefs.epitaxyPrefs === undefined || object(prefs.epitaxyPrefs));
    const epi = prefs.epitaxyPrefs ??= {};
    const pins = epi['starred-local-code-sessions'] ?? [];
    const slice = epi['dframe-local-slice'] ?? {pinnedOrder: [], homeProjectsPinnedOrder: []};
    requireShape(strings(pins) && object(slice) && strings(slice.pinnedOrder));
    epi['starred-local-code-sessions'] = replaceShared(pins, new Set(change.shared), change.order);
    epi['dframe-local-slice'] = {...slice, pinnedOrder: replaceShared(slice.pinnedOrder,
        new Set(change.shared.map(id => 'code:' + id)), change.order.map(id => 'code:' + id))};
    return result;
}

// This function runs in an empty, offline document. No Claude application
// scripts, cookies, credentials, extensions or sessions are loaded.
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
            if (typeof pin !== 'string' || localStorage.getItem('dframe-store') === null)
                throw Error('not-initialized');
            return {pin, local: Object.fromEntries(['dframe-store',
                'LSS-persisted.starred-local-code-sessions', 'LSS-persisted.dframe-local-slice',
                'ccd-sync-owner', 'ccd-sync-active', 'ccd-sync-quarantine', 'ccd-sync-pending:ccd/dframe-store']
                .map(key => [key, localStorage.getItem(key)]))};
        }
        await new Promise((resolve, reject) => {
            const transaction = db.transaction('keyval', 'readwrite', {durability: 'strict'});
            transaction.objectStore('keyval').put(raw.pin, 'store:pin-state:dframe-starred-code');
            transaction.oncomplete = resolve;
            transaction.onerror = transaction.onabort = () => reject(Error('write-failed'));
        });
        for (const key of ['dframe-store', 'LSS-persisted.starred-local-code-sessions',
            'LSS-persisted.dframe-local-slice', 'ccd-sync-pending:ccd/dframe-store']) {
            if (raw.local[key] === null) localStorage.removeItem(key);
            else localStorage.setItem(key, raw.local[key]);
        }
        return true;
    } finally { db.close(); }
}

function atomicJSON(file, data) {
    const temporary = file + '.graft-' + crypto.randomUUID();
    try {
        fs.writeFileSync(temporary, JSON.stringify(data), {mode: 0o600, flag: 'wx'});
        fs.renameSync(temporary, file);
    } finally { try { fs.unlinkSync(temporary); } catch {} }
}

async function run() {
    const {app, BrowserWindow, session, protocol} = require('electron');
    const requestPath = process.argv[1];
    const request = parse(fs.readFileSync(requestPath, 'utf8'));
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
    app.dock?.hide();
    const opened = [];
    try {
        for (const [index, profile] of request.profiles.entries()) {
            const scratch = path.join(request.scratch, 'profile-' + index);
            fs.mkdirSync(scratch, {recursive: true, mode: 0o700});
            for (const name of ['Local Storage', 'IndexedDB']) {
                const original = path.join(profile.path, name);
                if (!fs.statSync(original).isDirectory()) throw Error('not-initialized');
                fs.symlinkSync(original, path.join(scratch, name));
            }
            const ses = session.fromPath(scratch, {cache: false});
            ses.setPermissionRequestHandler((contents, permission, callback) => callback(false));
            const origins = ['https://claude.ai', 'app://localhost'];
            for (const scheme of ['https', 'http', 'app']) await ses.protocol.handle(scheme, req => {
                if (!origins.some(origin => req.url === origin + '/')) return new Response('', {status: 403});
                return new Response('<!doctype html><title>Sidebar storage</title>', {headers: {
                    'Content-Type': 'text/html', 'Content-Security-Policy': "default-src 'none'"}});
            });
            const window = new BrowserWindow({show: false, webPreferences: {
                session: ses, sandbox: true, contextIsolation: true, nodeIntegration: false}});
            const evaluate = (action, raw) => window.webContents.executeJavaScript(
                '(' + storageOperation.toString() + ')(' + JSON.stringify(action) + ',' + JSON.stringify(raw) + ')');
            const candidates = [];
            for (const origin of origins) {
                await window.loadURL(origin + '/');
                try {
                    const raw = await evaluate('read', null);
                    candidates.push({origin, raw});
                } catch (error) {
                    if (!String(error).includes('not-initialized')) throw error;
                }
            }
            requireShape(candidates.length === 1);
            const {origin, raw} = candidates[0];
            await window.loadURL(origin + '/');
            const prefsPath = path.join(profile.path, 'claude_desktop_config.json');
            let prefsText = null;
            if (fs.existsSync(prefsPath)) {
                requireShape(fs.lstatSync(prefsPath).isFile());
                prefsText = fs.readFileSync(prefsPath, 'utf8');
            }
            const prefs = prefsText === null ? {} : parse(prefsText);
            const state = snapshot(raw);
            state.fingerprint = digest({raw, prefsText, origin});
            const entry = {profile, window, ses, evaluate, raw, prefs, prefsText, prefsPath, state, origin};
            opened.push(entry);
            if (request.action === 'write') {
                requireShape(profile.change.expected === state.fingerprint);
                entry.next = patch(raw, profile.change);
                entry.nextPrefs = patchPreferences(prefs, profile.change);
            }
        }
        if (request.action === 'write') {
            // Preflight every participant before the first write. The saved
            // values also make an interrupted pass recoverable without copying
            // another account's browser database over this one.
            atomicJSON(request.backup, opened.map(item => ({profile: item.profile.path,
                origin: item.origin, raw: item.raw,
                preferences: item.prefs.preferences?.epitaxyPrefs ?? null})));
            for (const item of opened) {
                if ((fs.existsSync(item.prefsPath) ? fs.readFileSync(item.prefsPath, 'utf8') : null) !== item.prefsText)
                    throw Error('changed-during-sync');
                try {
                    await item.evaluate('write', item.next);
                    atomicJSON(item.prefsPath, item.nextPrefs);
                    await item.ses.flushStorageData();
                    const readBack = await item.evaluate('read', null);
                    requireShape(JSON.stringify(readBack) === JSON.stringify(item.next));
                } catch (error) {
                    await item.evaluate('write', item.raw);
                    if (item.prefsText !== null) atomicJSON(item.prefsPath, item.prefs);
                    else if (fs.existsSync(item.prefsPath)) fs.unlinkSync(item.prefsPath);
                    throw error;
                }
            }
        }
        atomicJSON(request.output, {ok: true, profiles: opened.map(item => ({path: item.profile.path,
            ...item.state, ...(request.action === 'write' ? snapshot(item.next) : {})}))});
    } catch (error) {
        atomicJSON(request.output, {ok: false, error: String(error.message ?? error).slice(0,300)});
    } finally {
        for (const item of opened) item.window.destroy();
        clearTimeout(timeout);
        app.quit();
    }
}
if (process.versions.electron) run().catch(() => require('electron').app.exit(1));
else module.exports = {replaceShared, decode, snapshot, patch, patchPreferences};
"""#
}
