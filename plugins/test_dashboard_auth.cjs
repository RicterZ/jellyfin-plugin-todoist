// Execute the dashboard's real request helper with legacy authentication disabled.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(__dirname + '/Jellyfin.Plugin.Todoist/Web/config.html', 'utf8');
const script = source.split('<script>')[1].split('</script>')[0];
let token = 'test-session-token';
let status = 200;
let calls = [];
const element = { addEventListener() {}, querySelector() { return element; } };
const context = {
    document: { querySelector() { return element; } },
    ApiClient: { accessToken: () => token, getUrl: path => '/jellyfin/' + path },
    fetch: async (url, options) => {
        calls.push({ url, ...options });
        assert.equal(options.headers['X-Emby-Token'], undefined);
        assert.ok(options.headers.Authorization.startsWith('MediaBrowser '));
        assert.ok(options.headers.Authorization.endsWith('Token="' + token + '"'));
        return { ok: status === 200, status, json: async () => ({ Url: 'https://todoist.com/oauth/authorize' }) };
    }
};
vm.createContext(context);
vm.runInContext(script.replace('    async function api(', '    globalThis.api = api;\n    async function api('), context);
(async () => {
    const result = await context.api('Authorize?dashboard=true', 'POST');
    assert.equal(result.url, 'https://todoist.com/oauth/authorize');
    assert.equal(calls[0].url, '/jellyfin/Todoist/Authorize?dashboard=true');
    assert.equal(calls[0].method, 'POST');
    for (const path of ['Status', 'Projects', 'Connections']) await context.api(path);
    await context.api('Project', 'POST', { ProjectId: 'project' });
    assert.equal(calls.at(-1).body, '{"ProjectId":"project"}');
    await context.api('Authorization', 'DELETE');
    status = 401;
    await assert.rejects(context.api('Status'), /登录已过期或认证失败/);
    const count = calls.length;
    token = '';
    await assert.rejects(context.api('Status'), /登录已过期/);
    assert.equal(calls.length, count);
    console.log('PASS: dashboard authorization, status, projects, connections, save, disconnect and expired sessions');
})().catch(error => { console.error(error); process.exitCode = 1; });
