import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const html = readFileSync(new URL('./index.html', import.meta.url), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
const fixtures = script.slice(script.indexOf('  const files = ['), script.indexOf('  const byId ='));

function functionSource(name) {
  const start = script.indexOf(`  function ${name}(`);
  assert.notEqual(start, -1, name);
  const end = script.indexOf('\n  ', start + 1);
  return script.slice(start, end < 0 ? undefined : end);
}

function matchingContext(query, settings = {}) {
  const context = vm.createContext({ query, console });
  vm.runInContext(`${fixtures}
    const options = { case: false, word: false, regex: false, ...${JSON.stringify(settings)} };
    const $ = () => ({ value: query });
    ${functionSource('ranges')}
    ${functionSource('collectHits')}
  `, context);
  return context;
}

function evaluate(source, context) {
  return JSON.parse(JSON.stringify(vm.runInContext(source, context)));
}

test('HTML内のJavaScript全体が構文解析でき、外部依存を持たない', () => {
  assert.doesNotThrow(() => new vm.Script(script));
  assert.match(html, /connect-src 'none'/);
  assert.doesNotMatch(html, /<script[^>]+src=|<link[^>]+href=/i);
  assert.match(html, /外観・操作モック/);
});

test('画面部品とアイコンのIDが衝突せず、固定参照先が存在する', () => {
  const ids = [...html.matchAll(/<[a-z][^>]*\sid="([^"]+)"/g)].map(match => match[1]);
  assert.equal(new Set(ids).size, ids.length);
  for (const match of script.matchAll(/\$\('([^']+)'\)/g)) {
    assert.ok(ids.includes(match[1]), match[1]);
  }
  for (const match of html.matchAll(/href="#([^"]+)"/g)) {
    assert.ok(ids.includes(match[1]), match[1]);
  }
});

test('通常検索は記号を正規表現として解釈しない', () => {
  const context = matchingContext('a.b');
  assert.deepEqual(evaluate('ranges("a.b aXb", query)', context), [{ start: 0, end: 3 }]);
  assert.deepEqual(evaluate('ranges("[x] x", "[x]")', context), [{ start: 0, end: 3 }]);
});

test('単語単位と大文字小文字のオプションを適用する', () => {
  const context = matchingContext('foo', { word: true });
  assert.deepEqual(evaluate('ranges("foo food Foo", query)', context), [
    { start: 0, end: 3 }, { start: 9, end: 12 },
  ]);
  assert.deepEqual(evaluate('ranges("foo food Foo", query, {case:true,word:true,regex:false})', context), [
    { start: 0, end: 3 },
  ]);
});

test('空語・無効な正規表現・0文字一致を扱う', () => {
  const context = matchingContext('');
  assert.deepEqual(evaluate('ranges("abc", "")', context), []);
  assert.deepEqual(evaluate('ranges("abc", "[", {regex:true})', context), []);
  assert.deepEqual(evaluate('ranges("a😀", "(?:)", {regex:true})', context), [
    { start: 0, end: 0 }, { start: 1, end: 1 }, { start: 3, end: 3 },
  ]);
});

test('初期検索は3ファイル5件、簡易定義は1候補になる', () => {
  const context = matchingContext('GetOrder');
  const hits = evaluate('collectHits("search")', context);
  assert.equal(hits.length, 5);
  assert.equal(new Set(hits.map(hit => hit.file)).size, 3);
  context.query = 'GetOrderAsync';
  const definitions = evaluate('collectHits("definition")', context);
  assert.equal(definitions.length, 1);
  assert.equal(definitions[0].file, 'service');
  assert.equal(definitions[0].line, 13);
  assert.equal(evaluate('collectHits("references")', context).length, 4);
});

test('C/C++・ヘッダーのサンプルを保持する', () => {
  const context = matchingContext('parse_order_id');
  const samples = evaluate('files.filter(f=>["c","cpp","header"].includes(f.id)).map(f=>({name:f.name,language:f.language}))', context);
  assert.deepEqual(samples, [
    { name: 'parser.cpp', language: 'C++' },
    { name: 'parser.h', language: 'C++' },
    { name: 'main.c', language: 'C' },
  ]);
  const hits = evaluate('collectHits("search")', context);
  assert.deepEqual(hits.map(hit => hit.file), ['cpp', 'header']);
});

test('最初の前一致は最後、次一致は先頭、端では折り返す', () => {
  const context = vm.createContext({});
  vm.runInContext(`
    const state={findIndex:-1,line:1};
    const findHits=()=>[{line:3},{line:8},{line:13}];
    const renderCode=()=>{};
    const updateFindCount=()=>{};
    const revealLine=()=>{};
    ${functionSource('nextFind')}
  `, context);
  assert.equal(evaluate('nextFind(-1); state.line', context), 13);
  assert.equal(evaluate('nextFind(1); state.line', context), 3);
  assert.equal(evaluate('state.findIndex=-1; nextFind(1); state.line', context), 3);
});
