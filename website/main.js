'use strict';
// A local, illustrative animation only: no screenshot, microphone or model request.
const trigger = document.querySelector('#demo-trigger');
const ring = document.querySelector('#ring-progress');
const subtitle = document.querySelector('#demo-subtitle');
const label = document.querySelector('#demo-label');
const description = document.querySelector('#demo-description');
let frame = 0;
trigger.addEventListener('click', () => {
  if (trigger.disabled) return;
  trigger.disabled = true;
  subtitle.classList.add('is-hidden');
  label.textContent = '正在长按 X…';
  description.textContent = '示意动画，不会实际截屏或发送请求';
  const start = performance.now();
  function tick(now) {
    const progress = Math.min(1, (now - start) / 1500);
    ring.style.strokeDashoffset = String(100 - progress * 100);
    if (progress < 1) frame = requestAnimationFrame(tick);
    else {
      subtitle.classList.remove('is-hidden');
      label.textContent = '译文，轻轻落在视野下方';
      description.textContent = '演示已完成 · 点击 X 再看一次';
      trigger.disabled = false;
    }
  }
  frame = requestAnimationFrame(tick);
});
window.addEventListener('pagehide', () => cancelAnimationFrame(frame));
