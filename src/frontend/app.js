/** @file app.js
 * @brief InfoBoardの画面、編集状態、同期およびMasonry配置。
 */
'use strict';
const POLL_MS = 5000;
const HEARTBEAT_MS = 10000;
const CARD_WIDTH = 360;
const GAP = 16;
const COLORS = {default: '標準', gray: 'グレー', red: 'レッド', orange: 'オレンジ', yellow: 'イエロー', green: 'グリーン', blue: 'ブルー', purple: 'パープル'};
const ui = Object.fromEntries(['search', 'sort', 'sortCancel', 'archive', 'add', 'status', 'board', 'empty', 'heading', 'editor', 'editForm', 'editorTitle', 'leaseStatus', 'title', 'owner', 'body', 'palette', 'editTab', 'previewTab', 'panes', 'preview', 'editError', 'cancel', 'save'].map(id => [id, document.getElementById(id)]));
let snapshot = {cards: [], orderVersion: 0, sorting: false};
let archived = false;
let sorting = null;
let edit = null;
let selectedColor = 'default';
let online = false;
let busy = false;
let polling = false;
let generation = 0;
let signature = '';
let dragId = null;
let layoutFrame = 0;
const expanded = new Set();

/** @brief DOM要素を生成する。
 * @param [in] tag 要素名
 * @param [in] text 表示文字列
 * @param [in] cls CSSクラス
 * @return DOM要素
 */
function element(tag, text = '', cls = '') {
  const node = document.createElement(tag);
  node.textContent = text;
  node.className = cls;
  return node;
}

/** @brief ボタンと操作を生成する。
 * @param [in] text ラベル
 * @param [in] action 操作
 * @param [in] disabled 無効状態
 * @return ボタン
 */
function button(text, action, disabled = false) {
  const node = element('button', text);
  node.type = 'button';
  node.disabled = disabled;
  node.addEventListener('click', action);
  return node;
}

/** @brief 同一オリジンAPIを呼び出す。
 * @param [in] data 変更要求。省略時は取得
 * @return JSONレスポンス
 */
async function api(data) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 12000);
  try {
    const options = {cache: 'no-store', credentials: 'same-origin', signal: controller.signal, headers: {Accept: 'application/json'}};
    if (data) {
      options.method = 'POST';
      options.headers['Content-Type'] = 'application/json';
      options.headers['X-InfoBoard'] = '1';
      options.body = JSON.stringify(data);
    }
    const response = await fetch('api/board', options);
    const result = await response.json().catch(() => ({}));
    if (!response.ok) {
      const error = new Error(result.message || `サーバーエラー (${response.status})`);
      error.status = response.status;
      throw error;
    }
    return result;
  } finally {
    clearTimeout(timeout);
  }
}

/** @brief 接続状態と通知を表示する。
 * @param [in] message 状態文字列
 * @param [in] error エラー状態
 */
function status(message, error = false) {
  ui.status.textContent = message;
  ui.status.classList.toggle('error', error);
}

/** @brief Markdownを安全なHTMLへ変換する。
 * @param [in] node 描画先
 * @param [in] source Markdown原文
 */
function markdown(node, source) {
  const parsed = window.marked.parse(source, {gfm: true, breaks: false});
  node.innerHTML = window.DOMPurify.sanitize(parsed, {USE_PROFILES: {html: true}, FORBID_TAGS: ['style', 'img', 'form', 'input', 'button', 'iframe', 'video', 'audio', 'source'], FORBID_ATTR: ['style', 'class', 'id', 'name', 'target', 'src', 'srcset'], ALLOW_DATA_ATTR: false});
  for (const link of node.querySelectorAll('a[href]')) {
    link.target = '_blank';
    link.rel = 'noopener noreferrer';
  }
}

/** @brief 表示用日時を日本時間へ変換する。
 * @param [in] value ISO日時
 * @return 日時文字列
 */
function dateLabel(value) {
  return new Date(value).toLocaleString('ja-JP', {timeZone: 'Asia/Tokyo', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit'});
}

/** @brief 現在表示するカードを抽出する。
 * @return 表示順に並べたカード配列
 */
function visibleCards() {
  const terms = ui.search.value.normalize('NFKC').toLowerCase().trim().split(/\s+/).filter(Boolean);
  const cards = snapshot.cards.filter(card => {
    const haystack = [card.title, card.owner, card.body].join('\n').normalize('NFKC').toLowerCase();
    return card.archived === archived && terms.every(term => haystack.includes(term));
  });
  if (!sorting) return cards;
  const byId = new Map(cards.map(card => [card.id, card]));
  return sorting.order.map(id => byId.get(id)).filter(Boolean);
}

/** @brief 画面幅とカードの実寸から最短列へ配置する。 */
function layout() {
  layoutFrame = 0;
  const width = ui.board.clientWidth;
  if (!width) return;
  const count = Math.max(1, Math.floor((width + GAP) / (CARD_WIDTH + GAP)));
  const cardWidth = (width - GAP * (count - 1)) / count;
  const heights = Array(count).fill(0);
  for (const card of ui.board.children) {
    card.style.width = `${cardWidth}px`;
    const body = card.querySelector('.card-body');
    const more = card.querySelector('.more');
    if (more) {
      const overflowing = body.scrollHeight > 240;
      more.hidden = !overflowing && !expanded.has(card.dataset.id);
    }
    const column = heights.indexOf(Math.min(...heights));
    card.style.left = `${column * (cardWidth + GAP)}px`;
    card.style.top = `${heights[column]}px`;
    heights[column] += card.offsetHeight + GAP;
  }
  ui.board.style.height = `${Math.max(0, Math.max(...heights) - GAP)}px`;
}

/** @brief レイアウト更新を次の描画にまとめる。 */
function scheduleLayout() {
  if (!layoutFrame) { layoutFrame = requestAnimationFrame(layout); }
}

/** @brief 並べ替え中のドラッグ位置を変更する。
 * @param [in] id 移動対象
 * @param [in] target 移動先のカード
 * @param [in] after 移動先の後ろに挿入するか
 */
function move(id, target, after) {
  if (!sorting || sorting.lost || id === target || !sorting.order.includes(id) || !sorting.order.includes(target)) return;
  const order = sorting.order.filter(item => item !== id);
  order.splice(order.indexOf(target) + (after ? 1 : 0), 0, id);
  sorting.order = order;
  render(true);
}

/** @brief アーカイブ・復元などのカード操作を行う。
 * @param [in] card 操作対象
 * @param [in] action 操作名
 * @param [in] question 確認文
 */
async function cardAction(card, action, question) {
  if (busy || !online || !window.confirm(question)) return;
  await mutate({action, id: card.id, version: card.version});
}

/** @brief カードのDOMを生成する。
 * @param [in] card カード
 * @param [in] index 表示順
 * @param [in] length 表示件数
 * @return カードDOM
 */
function makeCard(card, index, length) {
  const article = element('article');
  article.dataset.id = card.id;
  article.dataset.color = card.color;
  article.classList.toggle('editing', card.editing);
  const head = element('div', '', 'card-head');
  head.append(element('h3', card.title));
  if (sorting) {
    const tools = element('div', '', 'sort-tools');
    const handle = element('span', '⠿', 'handle');
    handle.draggable = !sorting.lost;
    handle.title = 'ドラッグして移動';
    handle.addEventListener('dragstart', event => {
      dragId = card.id;
      article.classList.add('dragging');
      event.dataTransfer.effectAllowed = 'move';
      event.dataTransfer.setData('text/plain', card.id);
    });
    handle.addEventListener('dragend', () => {
      dragId = null;
      article.classList.remove('dragging');
      for (const node of ui.board.children) { node.classList.remove('drop'); }
    });
    article.addEventListener('dragover', event => {
      if (!dragId || dragId === card.id) return;
      event.preventDefault();
      article.classList.add('drop');
    });
    article.addEventListener('dragleave', () => article.classList.remove('drop'));
    article.addEventListener('drop', event => {
      event.preventDefault();
      const rect = article.getBoundingClientRect();
      move(dragId, card.id, event.clientY > rect.top + rect.height / 2);
      dragId = null;
    });
    const up = button('↑', () => move(card.id, sorting.order[index - 1], false), index === 0 || sorting.lost);
    up.setAttribute('aria-label', '前へ移動');
    const down = button('↓', () => move(card.id, sorting.order[index + 1], true), index === length - 1 || sorting.lost);
    down.setAttribute('aria-label', '後ろへ移動');
    tools.append(handle, up, down);
    head.append(tools);
  } else {
    const menu = element('details', '', 'menu');
    const summary = element('summary', '⋮');
    summary.setAttribute('aria-label', `${card.title}の操作メニュー`);
    const items = element('div', '', 'menu-items');
    const locked = card.editing || !online || busy;
    if (card.archived) {
      items.append(button('通常一覧に戻す', () => cardAction(card, 'unarchive', 'このカードを通常一覧へ戻しますか？'), locked || snapshot.sorting));
    } else {
      items.append(button('編集', () => openEditor(card), locked));
      items.append(button('直前の状態に戻す', () => cardAction(card, 'restore', '現在と直前の内容を入れ替えますか？'), locked || !card.hasPrevious));
      items.append(button('アーカイブ', () => cardAction(card, 'archive', 'このカードをアーカイブしますか？'), locked || snapshot.sorting));
    }
    menu.append(summary, items);
    menu.addEventListener('toggle', () => {
      if (!menu.open) return;
      for (const other of ui.board.querySelectorAll('details[open]')) {
        if (other !== menu) { other.open = false; }
      }
    });
    head.append(menu);
  }
  const owner = element('div', `オーナー：${card.owner || '—'}`, 'owner');
  if (card.editing) { owner.append(element('span', '● 編集中', 'editing-label')); }
  const body = element('div', '', 'markdown card-body');
  markdown(body, card.body);
  body.classList.toggle('collapsed', !expanded.has(card.id));
  const more = button(expanded.has(card.id) ? '折りたたむ ↑' : 'もっと見る ↓', () => {
    if (expanded.has(card.id)) {
      expanded.delete(card.id);
    } else {
      expanded.add(card.id);
    }
    const isExpanded = expanded.has(card.id);
    body.classList.toggle('collapsed', !isExpanded);
    more.textContent = isExpanded ? '折りたたむ ↑' : 'もっと見る ↓';
    more.setAttribute('aria-expanded', String(isExpanded));
    scheduleLayout();
  });
  more.className = 'more';
  more.setAttribute('aria-expanded', String(expanded.has(card.id)));
  article.append(head, owner, body, more, element('time', `${dateLabel(card.updatedAt)} 更新`, 'updated'));
  return article;
}

/** @brief 一覧の変更時のみ再描画し、展開状態を維持する。
 * @param [in] force 強制再描画
 */
function render(force = false) {
  const cards = visibleCards();
  const next = JSON.stringify([cards, archived, online, busy, snapshot.sorting, sorting?.order, sorting?.lost]);
  ui.sort.textContent = sorting ? '完了' : '並べ替え';
  ui.sort.disabled = !online || busy || (sorting ? sorting.lost : archived || !!ui.search.value.trim() || snapshot.sorting || cards.length < 2);
  ui.sortCancel.hidden = !sorting;
  ui.sortCancel.disabled = busy;
  ui.add.disabled = !online || busy || archived || !!sorting || snapshot.sorting;
  ui.archive.disabled = !!sorting || busy;
  ui.archive.textContent = archived ? '通常一覧' : 'アーカイブ';
  ui.search.disabled = !!sorting;
  ui.heading.textContent = archived ? 'アーカイブ一覧' : 'カード一覧';
  ui.empty.textContent = ui.search.value.trim() ? '該当するカードはありません。' : archived ? 'アーカイブ済みカードはありません。' : 'カードがありません。追加ボタンから登録してください。';
  ui.empty.hidden = cards.length > 0 || !online;
  if (!force && signature === next) return;
  signature = next;
  ui.board.replaceChildren(...cards.map((card, i) => makeCard(card, i, cards.length)));
  scheduleLayout();
}

/** @brief 最新の保存内容と他の利用者の操作状態を取得する。 */
async function poll() {
  if (polling || busy || sorting) return;
  polling = true;
  const start = generation;
  try {
    const data = await api();
    if (start !== generation || busy || sorting) return;
    snapshot = data;
    online = true;
    status(snapshot.sorting ? '他の利用者が並べ替え中です。' : `${snapshot.cards.filter(card => card.archived === archived).length} 件 · 自動同期中`);
    render();
  } catch (error) {
    if (start !== generation) return;
    online = false;
    status('接続できません。表示は最後に取得した内容です。', true);
    render();
  } finally {
    polling = false;
  }
}

/** @brief 一般操作を実行する。
 * @param [in] data 変更要求
 */
async function mutate(data) {
  if (busy) return;
  busy = true;
  ++generation;
  render();
  let failure = '';
  try {
    await api(data);
  } catch (error) {
    failure = error.message;
  } finally {
    busy = false;
    await poll();
    render();
    if (failure) { status(failure, true); }
  }
}

/** @brief 現在の入力内容を取得する。
 * @return カード内容
 */
function draft() {
  return {title: ui.title.value, owner: ui.owner.value, body: ui.body.value, color: selectedColor};
}

/** @brief 背景色の選択状態とプレビューを更新する。 */
function refreshPreview() {
  markdown(ui.preview, ui.body.value);
  ui.preview.dataset.color = selectedColor;
  for (const swatch of ui.palette.children) { swatch.setAttribute('aria-checked', String(swatch.dataset.color === selectedColor)); }
}

/** @brief スマートフォン用の編集・プレビュータブを切り替える。
 * @param [in] tab タブ名
 */
function setTab(tab) {
  ui.panes.dataset.tab = tab;
  ui.editTab.setAttribute('aria-pressed', String(tab === 'edit'));
  ui.previewTab.setAttribute('aria-pressed', String(tab === 'preview'));
}

/** @brief カード編集を開始し、既存カードのリースを取得する。
 * @param [in] card 対象。省略時は新規作成
 */
async function openEditor(card) {
  if (edit || busy || sorting || !online) return;
  busy = true;
  ++generation;
  try {
    let value = {title: '', owner: '', body: '', color: 'default'};
    if (card) {
      const result = await api({action: 'acquire', id: card.id});
      value = result.card;
      edit = {id: card.id, token: result.token, version: value.version, lost: false};
    } else {
      edit = {id: null, lost: false};
    }
    ui.title.value = value.title;
    ui.owner.value = value.owner;
    ui.body.value = value.body;
    selectedColor = value.color;
    edit.initial = JSON.stringify(draft());
    ui.editorTitle.textContent = card ? 'カード編集' : 'カード追加';
    ui.leaseStatus.textContent = card ? '編集権取得済み' : '新規作成';
    ui.editError.hidden = true;
    ui.save.disabled = false;
    ui.cancel.disabled = false;
    setTab('edit');
    refreshPreview();
    ui.editor.showModal();
    ui.title.focus();
  } catch (error) {
    status(error.message, true);
  } finally {
    busy = false;
    render();
  }
}

/** @brief ダイアログを閉じ、必要ならリースを解放する。
 * @param [in] release ロックを解放するか
 */
async function closeEditor(release) {
  const current = edit;
  edit = null;
  ui.editor.close();
  ++generation;
  if (release && current?.id) {
    try { await api({action: 'release', id: current.id, token: current.token}); } catch (error) { status('編集ロックは有効期限経過後に解除されます。', true); }
  }
  await poll();
  render();
}

/** @brief 未保存入力の破棄を確認する。 */
function cancelEditor() {
  if (!edit || busy) return;
  if (JSON.stringify(draft()) !== edit.initial && !window.confirm('未保存の変更を破棄しますか？')) return;
  void closeEditor(true);
}

/** @brief 編集エラーを表示する。
 * @param [in] message エラー文字列
 */
function editError(message) {
  ui.editError.textContent = message;
  ui.editError.hidden = false;
}

/** @brief 編集内容を保存する。
 * @param [in] event フォームイベント
 */
async function saveEditor(event) {
  event.preventDefault();
  if (!edit || edit.lost || busy) return;
  busy = true;
  ++generation;
  ui.save.disabled = true;
  ui.cancel.disabled = true;
  try {
    const content = draft();
    const data = edit.id ? {action: 'save', id: edit.id, token: edit.token, version: edit.version, content} : {action: 'create', content};
    await api(data);
    await closeEditor(false);
  } catch (error) {
    if (error.status === 409 && edit?.id) {
      edit.lost = true;
      ui.leaseStatus.textContent = '編集権喪失';
    }
    editError(`${error.message} 入力内容は保持しています。`);
  } finally {
    busy = false;
    ui.cancel.disabled = false;
    ui.save.disabled = !!edit?.lost;
    await poll();
    render();
  }
}

/** @brief 並べ替えモードを開始または確定する。 */
async function sortToggle() {
  if (busy || !online) return;
  busy = true;
  ++generation;
  try {
    if (sorting) {
      if (sorting.lost) return;
      await api({action: 'sortSave', token: sorting.token, version: sorting.version, order: sorting.order});
      sorting = null;
    } else {
      const result = await api({action: 'sortAcquire'});
      // 取得前の表示と順序が異なる場合、保存前に一覧を取り直す。
      const latest = await api();
      snapshot = latest;
      sorting = {token: result.token, version: result.version, order: result.order, lost: false};
      status('並べ替え中：ドラッグまたは矢印で移動し、完了を押してください。');
    }
  } catch (error) {
    if (sorting && error.status === 409) { sorting.lost = true; }
    status(error.message, true);
  } finally {
    busy = false;
    await poll();
    render(true);
  }
}

/** @brief 並べ替えの変更を破棄する。 */
async function cancelSort() {
  if (!sorting || busy) return;
  const current = sorting;
  sorting = null;
  ++generation;
  try { await api({action: 'sortRelease', token: current.token}); } catch (error) { status('並べ替えロックは期限経過後に解除されます。', true); }
  await poll();
  render(true);
}

/** @brief 有効な操作リースを延長し、競合時には入力を保持する。 */
async function heartbeat() {
  if (busy) return;
  const current = edit?.id ? edit : sorting;
  if (!current || current.lost) return;
  const isEdit = current === edit;
  try {
    await api(isEdit ? {action: 'heartbeat', id: current.id, token: current.token} : {action: 'sortHeartbeat', token: current.token});
    if (current === edit) { ui.leaseStatus.textContent = '編集権取得済み'; }
  } catch (error) {
    if (current !== edit && current !== sorting) return;
    if (error.status === 409) {
      current.lost = true;
      if (isEdit) {
        ui.save.disabled = true;
        ui.leaseStatus.textContent = '編集権喪失';
        editError('編集権の期限が切れました。入力をコピーしてから開き直してください。');
      } else {
        status('並べ替え権限を失いました。キャンセルしてやり直してください。', true);
        render(true);
      }
    } else if (isEdit) {
      ui.leaseStatus.textContent = '通信不安定：編集権を確認中';
    } else {
      status('通信不安定：並べ替え権を確認中', true);
    }
  }
}

/** @brief 離脱時に可能な範囲でロックを解除する。 */
function releaseOnExit() {
  const data = edit?.id ? {action: 'release', id: edit.id, token: edit.token} : sorting ? {action: 'sortRelease', token: sorting.token} : null;
  if (data) {
    void fetch('api/board', {method: 'POST', headers: {'Content-Type': 'application/json', 'X-InfoBoard': '1'}, body: JSON.stringify(data), keepalive: true}).catch(() => {});
  }
}

/** @brief UIのイベントと定期同期を設定する。 */
function start() {
  if (!window.marked || !window.DOMPurify || !window.DOMPurify.isSupported) {
    status('Markdownライブラリが不足しているか、ブラウザーが未対応です。配備パッケージを確認してください。', true);
    ui.add.disabled = true;
    ui.sort.disabled = true;
    return;
  }
  ui.palette.setAttribute('role', 'group');
  for (const [color, name] of Object.entries(COLORS)) {
    const swatch = button('', () => { selectedColor = color; refreshPreview(); });
    swatch.className = 'swatch';
    swatch.dataset.color = color;
    swatch.title = name;
    swatch.setAttribute('role', 'radio');
    swatch.setAttribute('aria-label', name);
    ui.palette.append(swatch);
  }
  ui.palette.setAttribute('role', 'radiogroup');
  ui.palette.setAttribute('aria-label', '背景色');
  ui.add.addEventListener('click', () => openEditor());
  ui.archive.addEventListener('click', () => { archived = !archived; render(true); void poll(); });
  ui.search.addEventListener('input', () => render());
  ui.sort.addEventListener('click', sortToggle);
  ui.sortCancel.addEventListener('click', cancelSort);
  ui.body.addEventListener('input', refreshPreview);
  ui.editTab.addEventListener('click', () => setTab('edit'));
  ui.previewTab.addEventListener('click', () => setTab('preview'));
  ui.cancel.addEventListener('click', cancelEditor);
  ui.editor.addEventListener('cancel', event => { event.preventDefault(); cancelEditor(); });
  ui.editForm.addEventListener('submit', saveEditor);
  document.addEventListener('click', event => {
    for (const menu of ui.board.querySelectorAll('details[open]')) {
      if (!menu.contains(event.target)) { menu.open = false; }
    }
  });
  window.addEventListener('resize', scheduleLayout);
  window.addEventListener('pagehide', releaseOnExit);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) { void heartbeat(); void poll(); } });
  render(true);
  void poll();
  setInterval(poll, POLL_MS);
  setInterval(heartbeat, HEARTBEAT_MS);
}
start();
