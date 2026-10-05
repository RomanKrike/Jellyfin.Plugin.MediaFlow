import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { JSDOM, VirtualConsole } from 'jsdom';

const html = await readFile(new URL('../Jellyfin.Plugin.MediaFlow/Configuration/configPage.html', import.meta.url), 'utf8');
const strings = JSON.parse(await readFile(new URL('../Jellyfin.Plugin.MediaFlow/Localization/ru-RU.json', import.meta.url), 'utf8'));
const script = html.match(/<script>\s*([\s\S]*?)<\/script>/)?.[1]
    || html.match(/<script type="text\/javascript">\s*([\s\S]*?)<\/script>/)[1];
const torrent = { hash:'a'.repeat(40), name:'Kusuriya no Hitorigoto TV-1', category:'tv', progress:0.2, qbState:'downloading' };
const identity = { name:torrent.name, category:'tv', files:[], media:{ title:'Монолог фармацевта', year:2023, tmdbId:220542, posterPath:'/poster.jpg', seasons:[1] } };
const tick = () => new Promise(resolve => setTimeout(resolve, 10));

function fixture() {
    const dom = new JSDOM(html, { runScripts:'outside-only', url:'https://jellyfin.example/', virtualConsole:new VirtualConsole() });
    const { window } = dom;
    const pending = [];
    const state = { torrents:[{ ...torrent }], history:[] };
    window.Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, alert() {} };
    window.ApiClient = {
        getUrl:path => path,
        ajax:options => {
            if (options.url.includes('/overview?')) return Promise.resolve(structuredClone(state));
            if (options.url.includes('/details?')) return new Promise((resolve, reject) => pending.push({ resolve, reject, url:options.url }));
            return Promise.reject(new Response('{"message":"Episode review state is missing season/episode numbers."}', { status:400 }));
        }
    };
    window.eval(script.replace('queueMicrotask(showMediaFlowPage);', `strings = ${JSON.stringify(strings)}; window.mfTest = {
        api, apiErrorMessage, refreshOverview, hydrateRecentCards, renderQueue,
        loadMapping, previewMapping, saveMapping, invalidateMapping, readMappingRequest, renderMappingPreview,
        get cache() { return torrentDetailsCache; }
    };`));
    const titles = () => Array.from(window.document.querySelectorAll('[data-mf-title]')).map(x => x.textContent);
    return { dom, window, api:window.mfTest, pending, state, titles };
}

test('fetch Response and legacy HTTP errors expose the server message', async () => {
    const f = fixture();
    try {
        const response = new Response('{"message":"Missing episode"}', { status:400, statusText:'Bad Request' });
        assert.equal(await f.api.apiErrorMessage(response), 'Missing episode');
        assert.equal(response.bodyUsed, false);
        assert.equal(await f.api.apiErrorMessage({ responseText:'{"Message":"Legacy error"}', status:500 }), 'Legacy error');
        assert.equal(await f.api.apiErrorMessage(new Response('{"detail":"Problem details"}', { status:400 })), 'Problem details');
        assert.equal(await f.api.apiErrorMessage(new Response('Upstream unavailable', { status:503 })), 'Upstream unavailable');
        assert.equal(await f.api.apiErrorMessage(new Response('<html>proxy error</html>', { status:502 })), 'HTTP 502');
        assert.equal(await f.api.apiErrorMessage(new Error('Network unavailable')), 'Network unavailable');
        assert.equal(await f.api.apiErrorMessage({ status:401, statusText:'Unauthorized', text:async () => { throw new Error('unreadable'); } }), 'HTTP 401: Unauthorized');
        await assert.rejects(f.api.api('GET', 'MediaFlow/Admin/review/search'), { message:'Episode review state is missing season/episode numbers.', status:400 });
    } finally { f.dom.window.close(); }
});

test('refresh keeps TMDb titles, seasons, loaded posters and expanded files while details are pending', async () => {
    const f = fixture();
    try {
        await f.api.refreshOverview(false, true);
        await tick();
        assert.equal(f.pending.length, 1, 'overview and queue share one detail request');
        f.pending.shift().resolve(structuredClone(identity));
        await tick();
        assert.deepEqual(f.titles(), ['Монолог фармацевта (2023)', 'Монолог фармацевта (2023)']);
        const poster = f.window.document.querySelector('#mfTorrentCards img');
        f.window.document.querySelector('#mfTorrentCards [data-action="toggle-details"]').click();
        await tick();
        const seen = [];
        const observer = new f.window.MutationObserver(() => seen.push(...f.titles()));
        observer.observe(f.window.document.querySelector('#MediaFlowConfigPage'), { childList:true, subtree:true, characterData:true });
        f.state.torrents[0].progress = 0.4;
        // Expanded-card restoration awaits the details request. Inspect the DOM
        // while the network is deliberately stalled, before resolving refresh.
        const refresh = f.api.refreshOverview(false, true);
        await tick();
        assert.deepEqual(f.titles(), ['Монолог фармацевта (2023)', 'Монолог фармацевта (2023)']);
        assert.equal(f.window.document.querySelector('#mfTorrentCards img'), poster);
        assert.ok(f.window.document.querySelector('#mfTorrentCards .expanded'));
        assert.match(f.window.document.querySelector('#mfTorrentCards [data-mf-seasons]').textContent, /1/);
        assert.match(f.window.document.querySelector('#mfTorrentCards .mf-progress-row').textContent, /40.0%/);
        assert.equal(f.pending.length, 1);
        f.pending.shift().resolve({ ...identity, media:{ ...identity.media, title:'Updated title', seasons:[1, 2] } });
        await refresh;
        await tick();
        assert.deepEqual(f.titles(), ['Updated title (2023)', 'Updated title (2023)']);
        assert.ok(seen.every(title => title !== torrent.name), 'no transient release title during refresh');
        observer.disconnect();
        f.api.renderQueue();
        assert.equal(f.window.document.querySelector('#mfTorrentCards img'), poster, 'filter redraw also preserves poster');
    } finally { f.dom.window.close(); }
});

test('failed details refresh keeps the last identity and a later refresh updates it', async () => {
    const f = fixture();
    try {
        await f.api.refreshOverview(false);
        await tick();
        f.pending.shift().resolve(structuredClone(identity));
        await tick();
        await f.api.refreshOverview(false);
        await tick();
        f.pending.shift().reject(new Response('', { status:503 }));
        await tick();
        assert.deepEqual(f.titles(), ['Монолог фармацевта (2023)', 'Монолог фармацевта (2023)']);
        await f.api.refreshOverview(false);
        await tick();
        f.pending.shift().resolve({ ...identity, media:null });
        await tick();
        assert.deepEqual(f.titles(), [torrent.name, torrent.name], 'authoritative cleared identity is displayed');
        assert.equal(f.window.document.querySelectorAll('.mf-cover-slot img').length, 0);
    } finally { f.dom.window.close(); }
});

test('late responses cannot overwrite fresh identity or resurrect a removed torrent', async () => {
    const f = fixture();
    try {
        await f.api.refreshOverview(false);
        await tick();
        const old = f.pending.shift();
        await f.api.refreshOverview(false);
        await tick();
        f.pending.shift().resolve(structuredClone(identity));
        await tick();
        old.resolve({ ...identity, media:{ ...identity.media, title:'Stale identity' } });
        await tick();
        assert.deepEqual(f.titles(), ['Монолог фармацевта (2023)', 'Монолог фармацевта (2023)']);
        await f.api.refreshOverview(false);
        await tick();
        const removed = f.pending.shift();
        f.state.torrents = [];
        await f.api.refreshOverview(false);
        removed.resolve(structuredClone(identity));
        await tick();
        assert.equal(f.api.cache.size, 0);
        assert.deepEqual(f.titles(), []);
    } finally { f.dom.window.close(); }
});

test('cached cards beyond the initial hydration limit still receive fresh details', async () => {
    const f = fixture();
    try {
        f.state.torrents = Array.from({ length:10 }, (_, index) => ({ ...torrent, hash:String(index).repeat(40) }));
        const last = f.state.torrents[9];
        f.api.cache.set(last.hash, structuredClone(identity));
        await f.api.refreshOverview(false);
        await tick();
        const request = f.pending.find(x => x.url.includes(last.hash));
        assert.ok(request, 'known identity beyond the first eight cards is refreshed');
        request.resolve({ ...identity, media:{ ...identity.media, title:'Fresh title outside first eight' } });
        await tick();
        assert.equal(f.window.document.querySelector('#mfTorrentCards [data-torrent-hash="' + last.hash + '"] [data-mf-title]').textContent, 'Fresh title outside first eight (2023)');
    } finally { f.dom.window.close(); }
});

test('a delayed overview response cannot revert progress from a newer refresh', async () => {
    const f = fixture();
    try {
        const original = f.window.ApiClient.ajax;
        const requests = [];
        f.window.ApiClient.ajax = options => options.url.includes('/overview?')
            ? new Promise(resolve => requests.push(resolve)) : original(options);
        const old = f.api.refreshOverview(false);
        const fresh = f.api.refreshOverview(false);
        requests[1]({ torrents:[{ ...torrent, progress:0.7 }], history:[] });
        await fresh;
        requests[0]({ torrents:[{ ...torrent, progress:0.1 }], history:[] });
        await old;
        assert.match(f.window.document.querySelector('#mfTorrentCards .mf-progress-row').textContent, /70.0%/);
    } finally { f.dom.window.close(); }
});

function mockMappingApi(f) {
    const requests = [];
    f.window.ApiClient.ajax = options => {
        if (!options.url.includes('/episode-mapping')) return Promise.resolve(structuredClone(f.state));
        return new Promise((resolve, reject) => requests.push({ ...options, resolve, reject }));
    };
    f.window.document.querySelector('#mfMappingTorrent').innerHTML = `<option value="${torrent.hash}">TV torrent</option>`;
    return requests;
}
const rule = { releaseSeason:2, librarySeason:2, providerSeason:1, episodeOffset:24 };
const mappingData = { tmdbId:220542, rules:[rule] };
const previewData = { title:'Монолог фармацевта', canSave:true, previewToken:'checked', rows:[{
    file:'TV-2 01.mkv', title:'Mapped episode', imported:false,
    identity:{ release:{ season:2, episode:1 }, library:{ season:2, episode:1 }, provider:{ season:1, episode:25 } }
}] };

test('mapping preview shows separate numbering and save requires a checked unchanged draft', async () => {
    const f = fixture();
    try {
        const requests = mockMappingApi(f);
        const load = f.api.loadMapping(torrent.hash);
        requests.shift().resolve(mappingData); await load;
        assert.equal(f.window.document.querySelector('#mfMappingSave').disabled, true);
        const preview = f.api.previewMapping();
        const check = requests.shift();
        assert.deepEqual(JSON.parse(check.data), { tmdbId:220542, seasons:[rule] });
        check.resolve(previewData); await preview;
        const table = f.window.document.querySelector('#mfMappingRows').textContent;
        assert.match(table, /S02E01.*S02E01.*S01E25/);
        assert.equal(f.window.document.querySelector('#mfMappingSave').disabled, false);
        const save = f.api.saveMapping();
        const write = requests.shift();
        assert.deepEqual(JSON.parse(write.data), { tmdbId:220542, seasons:[rule], previewToken:'checked' });
        write.resolve({ success:true }); await save;
        assert.equal(f.window.document.querySelector('#mfMappingSave').disabled, true);
        assert.equal(requests.length, 0);
    } finally { f.dom.window.close(); }
});

test('editing while mapping preview is pending discards the stale reply', async () => {
    const f = fixture();
    try {
        const requests = mockMappingApi(f);
        const load = f.api.loadMapping(torrent.hash); requests.shift().resolve(mappingData); await load;
        const preview = f.api.previewMapping(); const pending = requests.shift();
        const offset = f.window.document.querySelector('[data-mapping-field="episodeOffset"]');
        offset.value = '12'; offset.dispatchEvent(new f.window.Event('input', { bubbles:true }));
        pending.resolve(previewData); await preview;
        assert.equal(f.window.document.querySelector('#mfMappingSave').disabled, true);
        assert.equal(f.window.document.querySelector('#mfMappingRows').textContent, '');
        await f.api.saveMapping();
        assert.equal(requests.length, 0, 'stale preview must not allow a save');
    } finally { f.dom.window.close(); }
});

test('switching torrents while loading rules cannot install the previous torrent draft', async () => {
    const f = fixture();
    try {
        const requests = mockMappingApi(f);
        const load = f.api.loadMapping(torrent.hash); const pending = requests.shift();
        f.window.document.querySelector('#mfMappingTorrent').dispatchEvent(new f.window.Event('change'));
        pending.resolve(mappingData); await load;
        assert.equal(f.window.document.querySelector('#mfMappingEditor').hidden, true);
        assert.equal(f.window.document.querySelector('#mfMappingSave').disabled, true);
    } finally { f.dom.window.close(); }
});

test('TV/Movie filter uses the configured categories', async () => {
    const f = fixture();
    try {
        f.window.__mfConfigCache = { QbittorrentTvCategory:'series', QbittorrentMovieCategory:'films' };
        f.state.torrents = [{ ...torrent, category:'series' }, { ...torrent, hash:'b'.repeat(40), name:'Movie', category:'films' }];
        await f.api.refreshOverview(false);
        f.window.document.querySelector('#mfTorrentKind').value = 'movie'; f.api.renderQueue();
        assert.equal(f.window.document.querySelectorAll('#mfTorrentCards .mf-rich-card').length, 1);
        assert.match(f.window.document.querySelector('#mfTorrentCards').textContent, /Movie/);
        assert.equal(f.window.document.querySelector('#mfTorrentCards [data-action="episode-mapping"]'), null);
        f.window.document.querySelector('#mfTorrentKind').value = 'tv'; f.api.renderQueue();
        assert.ok(f.window.document.querySelector('#mfTorrentCards [data-action="episode-mapping"]'));
    } finally { f.dom.window.close(); }
});
