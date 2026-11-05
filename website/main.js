(() => {
  'use strict';
  const scene = document.getElementById('sim');
  const choices = scene.querySelector('.mode-choices');
  const play = document.getElementById('replay');
  const state = document.getElementById('demo-state');
  const trigger = document.getElementById('trigger-key');
  const viewLabel = document.getElementById('view-label');
  const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  let timer;

  function reset() {
    window.clearTimeout(timer);
    scene.classList.remove('is-playing');
    play.disabled = false;
    play.querySelector('span').textContent = '播放演示';
    state.textContent = '固定示例，无需连接设备。';
  }

  choices.addEventListener('click', event => {
    const button = event.target.closest('button[data-choice]');
    if (!button || !choices.contains(button)) return;
    reset();
    const vr = button.dataset.choice === 'vr';
    scene.dataset.mode = vr ? 'vr' : 'desktop';
    for (const item of choices.querySelectorAll('button')) {
      item.setAttribute('aria-pressed', String(item === button));
    }
    trigger.textContent = vr ? '左手 X · 长按 1.5 秒' : 'Ctrl + Alt + T · 截取主屏';
    viewLabel.textContent = vr ? '头显视野' : '桌面窗口';
  });

  play.addEventListener('click', () => {
    if (play.disabled) return;
    const vr = scene.dataset.mode === 'vr';
    function finish() {
      scene.classList.remove('is-playing');
      play.disabled = false;
      play.querySelector('span').textContent = '重新播放';
      state.textContent = '字幕已显示：湖边休息处。';
    }
    if (reduceMotion.matches) { finish(); return; }
    scene.classList.add('is-playing');
    play.disabled = true;
    play.querySelector('span').textContent = '播放中';
    state.textContent = vr ? '正在演示长按触发…' : '正在演示快捷键截图…';
    timer = window.setTimeout(finish, vr ? 1500 : 450);
  });
  window.addEventListener('pagehide', reset);
  choices.hidden = false;
  play.hidden = false;
})();
