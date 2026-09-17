'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('Sources/Shared/SidebarStorage.swift', 'utf8');
const script = source.split('static let script = #"""\n')[1].split('\n"""#')[0];
const context = {require, module: {exports: {}}, process: {versions: {}}, structuredClone, Date};
vm.runInNewContext(script, context);
const api = context.module.exports;
const plain = value => JSON.parse(JSON.stringify(value));
let checks = 0;
function check(what, run) { run(); checks++; console.log('  ok    ' + what); }
const frame = {version: 1, state: {pinnedOrder: ['chat:remote', 'code:local_a', 'cowork:space', 'code:local_b'],
    sortByByMode: {code: 'recency', cowork: 'created'}, groupByByMode: {code: 'project'}, sidebarWidth: 290,
    customGroupsByScope: {'account/org': {groups: [{id: 'g', name: 'Keep this'}]}}, lastSidebarScopeKey: 'account/org'}};
const raw = {pin: JSON.stringify({version: 0, state: {starredIds: ['local_a', 'local_b', 'remote'], unrelated: true}, updatedAt: 1}),
    local: {'dframe-store': JSON.stringify(frame),
        'ccd-sync-owner': 'account', 'ccd-sync-active': '1', 'ccd-sync-quarantine': null,
        'ccd-sync-pending:ccd/dframe-store': null,
        'LSS-persisted.starred-local-code-sessions': JSON.stringify({value: ['local_a', 'local_b', 'local_private'], tabId: 'existing', timestamp: 5}),
        'LSS-persisted.dframe-local-slice': JSON.stringify({value: {pinnedOrder: frame.state.pinnedOrder,
            homeProjectsPinnedOrder: ['project']}, tabId: 'existing', timestamp: 10})}};
const change = {shared: ['local_a', 'local_b', 'local_c'], pins: ['local_b', 'local_c'],
    order: ['local_c', 'local_b'], sort: 'alpha'};
const changed = api.patch(raw, change);
check('an unpin and a new pin reach the authoritative pin set', () => {
    assert.deepEqual(plain(JSON.parse(changed.pin).state.starredIds), ['local_c', 'local_b', 'remote']);
});
check('only shared Code positions change in the mixed pinned section', () => {
    assert.deepEqual(JSON.parse(changed.local['dframe-store']).state.pinnedOrder,
        ['chat:remote', 'code:local_c', 'cowork:space', 'code:local_b']);
});
check('groups, layout, and Cowork sort choices survive a Code sort change', () => {
    const result = JSON.parse(changed.local['dframe-store']);
    const expected = structuredClone(frame); expected.state.pinnedOrder = result.state.pinnedOrder;
    expected.state.sortByByMode.code = 'alpha'; assert.deepEqual(result, expected);
});
check('a changed sort is queued as this account’s own pending preference edit', () => {
    assert.equal(changed.local['ccd-sync-pending:ccd/dframe-store'], 'account/org');
    assert.equal(changed.local['ccd-sync-owner'], 'account');
});
check('pin and manual order changes alone do not queue an account settings upload', () => {
    const result = api.patch(raw, {...change, sort: 'recency'});
    assert.equal(result.local['ccd-sync-pending:ccd/dframe-store'], null);
});
check('sort changes cannot cross a pending account or organization boundary', () => {
    for (const [key, value] of [['ccd-sync-owner', 'another-account'],
        ['ccd-sync-quarantine', '1'], ['ccd-sync-pending:ccd/dframe-store', 'account/another-org']]) {
        assert.throws(() => api.patch({...raw, local: {...raw.local, [key]: value}}, change));
    }
});
check('a sort change preserves the identity of an existing local migration', () => {
    const result = api.patch({...raw, local: {...raw.local,
        'ccd-sync-pending:ccd/dframe-store': 'account/org|migrate'}}, change);
    assert.equal(result.local['ccd-sync-pending:ccd/dframe-store'], 'account/org');
});
check('local-only profiles do not gain a server preference queue', () => {
    const local = {...raw.local, 'ccd-sync-owner': null, 'ccd-sync-active': null};
    assert.equal(api.patch({...raw, local}, change).local['ccd-sync-pending:ccd/dframe-store'], null);
    assert.throws(() => api.patch({...raw, local: {...local, 'ccd-sync-active': '1'}}, change));
});
check('the fallback pin list cannot bring an unpinned chat back', () => {
    assert.deepEqual(JSON.parse(changed.local['LSS-persisted.starred-local-code-sessions']).value,
        ['local_c', 'local_b', 'local_private']);
});
check('the fallback order retains home projects and other sidebar sections', () => {
    const value = JSON.parse(changed.local['LSS-persisted.dframe-local-slice']).value;
    assert.deepEqual(value.homeProjectsPinnedOrder, ['project']);
    assert.deepEqual(value.pinnedOrder, ['chat:remote', 'code:local_c', 'cowork:space', 'code:local_b']);
});
check('the original snapshot stays available for rollback', () => {
    assert.deepEqual(JSON.parse(raw.local['dframe-store']), frame);
});
check('clearing every shared pin clears both browser fallback lists', () => {
    const result = api.patch(raw, {...change, pins: [], order: []});
    assert.deepEqual(JSON.parse(result.pin).state.starredIds, ['remote']);
    assert.deepEqual(JSON.parse(result.local['LSS-persisted.starred-local-code-sessions']).value, ['local_private']);
    assert.deepEqual(JSON.parse(result.local['dframe-store']).state.pinnedOrder, ['chat:remote', 'cowork:space']);
});
check('a newer unknown pin schema is refused', () => {
    assert.throws(() => api.patch({...raw, pin: JSON.stringify({version: 1, state: {starredIds: []}})}, change));
});
check('a malformed fallback is refused before any write', () => {
    assert.throws(() => api.patch({...raw, local: {...raw.local, 'LSS-persisted.dframe-local-slice': '{'}}, change));
});
check('an ordering request cannot add a chat outside the shared set', () => {
    assert.throws(() => api.patch(raw, {...change, pins: ['local_private'], order: ['local_private']}));
});
check('a repeated pin cannot create duplicate sidebar entries', () => {
    assert.throws(() => api.patch(raw, {...change, order: ['local_b', 'local_b']}));
});
const prefs = {mcpServers: {keep: {command: 'example'}}, preferences: {bypassPermissionsOptInByAccount: {own: true},
    epitaxyPrefs: {'starred-local-code-sessions': ['local_a', 'local_private'],
        'dframe-local-slice': {pinnedOrder: ['code:local_a', 'chat:remote'], homeProjectsPinnedOrder: ['project']},
        'epitaxy-auto-mode-consented.own': true}}};
check('desktop preference changes preserve MCP definitions and permission choices', () => {
    const result = plain(api.patchPreferences(prefs, change));
    const expected = structuredClone(prefs);
    expected.preferences.epitaxyPrefs['starred-local-code-sessions'] = ['local_c', 'local_private', 'local_b'];
    expected.preferences.epitaxyPrefs['dframe-local-slice'].pinnedOrder = ['code:local_c', 'chat:remote', 'code:local_b'];
    assert.deepEqual(result, expected); assert.equal(prefs.preferences.epitaxyPrefs['starred-local-code-sessions'][0], 'local_a');
});
check('an unreadable desktop preference collection is refused', () => {
    assert.throws(() => api.patchPreferences({preferences: {epitaxyPrefs: 'invalid'}}, change));
});
check('missing fallback settings can be initialized without importing another account', () => {
    const result = plain(api.patchPreferences({}, change));
    assert.deepEqual(Object.keys(result.preferences), ['epitaxyPrefs']);
    assert.deepEqual(result.preferences.epitaxyPrefs['starred-local-code-sessions'], change.order);
});
check('a second application of the same change preserves the chosen pins and order', () => {
    const result = api.patch(changed, change);
    const a = plain(api.snapshot(changed)), b = plain(api.snapshot(result));
    assert.deepEqual([a.pins,a.order,a.sort], [b.pins,b.order,b.sort]);
});
console.log(`${checks}/${checks} sidebar storage checks passed`);
