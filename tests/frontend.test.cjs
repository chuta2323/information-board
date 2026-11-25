/** @file frontend.test.cjs
 * @brief カード移動とテキスト描画の回帰テスト。
 */
'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../src/frontend/app.js'), 'utf8');
const context = vm.createContext({document: {getElementById: () => null}});
assert.match(source, /\nstart\(\);\s*$/);
vm.runInContext(source.replace(/\nstart\(\);\s*$/, ''), context);
const order = ['a', 'b', 'c', 'd'];

test('先頭と末尾を双方向に移動', () => {
  assert.deepEqual(Array.from(context.reorderedCards(order, 'a', 'd')), ['b', 'c', 'd', 'a']);
  assert.deepEqual(Array.from(context.reorderedCards(order, 'd', 'a')), ['d', 'a', 'b', 'c']);
  assert.deepEqual(order, ['a', 'b', 'c', 'd']);
});
test('隣接カードと中間位置を双方向に移動', () => {
  assert.deepEqual(Array.from(context.reorderedCards(order, 'a', 'b')), ['b', 'a', 'c', 'd']);
  assert.deepEqual(Array.from(context.reorderedCards(order, 'b', 'a')), ['b', 'a', 'c', 'd']);
  assert.deepEqual(Array.from(context.reorderedCards(order, 'a', 'c')), ['b', 'c', 'a', 'd']);
  assert.deepEqual(Array.from(context.reorderedCards(order, 'd', 'b')), ['a', 'd', 'b', 'c']);
});
test('無効な移動は順序を維持', () => {
  assert.equal(context.reorderedCards(order, 'a', 'a'), order);
  assert.equal(context.reorderedCards(order, 'x', 'b'), order);
  assert.equal(context.reorderedCards(order, 'a', 'x'), order);
});
test('本文は改行・空白・記号・HTMLをそのまま扱う', () => {
  const body = '1行目\n2行目\n\n  字下げ\n\tタブ\n# 見出し\n<script>alert(1)</script>';
  const node = {textContent: '', set innerHTML(value) { throw new Error('HTML insertion'); }};
  context.showText(node, body);
  assert.equal(node.textContent, body);
  const css = fs.readFileSync(path.join(__dirname, '../src/frontend/style.css'), 'utf8');
  assert.match(css, /\.plain-text\{[^}]*white-space:pre-wrap/);
});
