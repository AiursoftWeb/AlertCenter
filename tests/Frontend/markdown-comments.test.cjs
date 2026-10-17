const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');

const bundle = path.resolve(__dirname,
    '../../src/Aiursoft.AlertCenter/wwwroot/node_modules/@aiursoft/uistack-markdown-ui/dist/index.global.js');
const context = vm.createContext({ console });
vm.runInContext(fs.readFileSync(bundle, 'utf8'), context);
const render = source => context.AiursoftMarkdownUi.renderMarkdown(source);

test('comment renderer escapes native HTML and script-capable links', () => {
    const source = '<script>window.commentXss=true</script>\n\n</textarea><img src=x onerror=alert(1)>\n\n' +
        '[javascript](javascript:alert(1))\n\n[data](data:text/html;base64,PHNjcmlwdD4=)\n\n' +
        '![image](javascript:alert(1))';
    const html = render(source);
    assert(html.includes('&lt;script&gt;'));
    assert(!/<script\b|<img\b|<textarea\b/i.test(html));
    assert(!/(href|src)="(?:javascript:|data:text\/html)/i.test(html));
});

test('preview and reader renderer preserve useful Markdown formatting safely', () => {
    const html = render('# 排查\n\n**证据**\n\n- 数据库\n- 网络\n\n' +
        '```sql\nSELECT 1;\n```\n\n| 指标 | 值 |\n| --- | --- |\n| 错误 | 1 |\n\n' +
        '[runbook](https://example.com/runbook)');
    assert(html.includes('<h1>排查</h1>'));
    assert(html.includes('<strong>证据</strong>'));
    assert(html.includes('<ul>'));
    assert(html.includes('<table>'));
    assert(html.includes('hljs'));
    assert(html.includes('href="https://example.com/runbook"'));
});

test('code blocks never become executable markup', () => {
    const html = render('```html\n<script>alert(1)</script>\n<img onerror=alert(1)>\n```');
    assert(!/<script\b|<img\b/i.test(html));
    assert(html.includes('&lt;'));
});


test('incident evidence preserves uploaded images, diagrams and formulas for reader enhancement', () => {
    const html = render('![Evidence](/download/incident-images/screenshot.png)\n\n' +
        '```mermaid\ngraph TD; A-->B;\n```\n\n$E=mc^2$\n\n$$\na^2+b^2=c^2\n$$');
    assert(html.includes('src="/download/incident-images/screenshot.png"'));
    assert(html.includes('language-mermaid'));
    assert(html.includes('class="math-inline"'));
    assert(html.includes('class="math-block"'));
});
