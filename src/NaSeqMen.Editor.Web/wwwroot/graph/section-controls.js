// SPDX-License-Identifier: MIT
import { t } from '../i18n.js';

// Tabler chevron-down, player-play, player-pause and bug.
const paths = {
  collapse: 'M6 9l6 6l6 -6',
  play: 'M7 4v16l13 -8z',
  pause: 'M9 5v14m6 -14v14',
  debug: 'M9 9v-1a3 3 0 0 1 6 0v1m-3 11a4 4 0 0 0 4 -4v-5a2 2 0 0 0 -2 -2h-4a2 2 0 0 0 -2 2v5a4 4 0 0 0 4 4zm0 0v-7m-8 0h4m8 0h4m-14 -5l2 2m8 0l2 -2m-10 9l-2 2m10 -2l2 2'
};

export function sectionControls(svg, section, label, toggle, setMode) {
  const choices = ['collapse', 'play', 'pause', 'debug'];
  return choices.map((choice, index) => {
    const collapse = choice === 'collapse';
    const x = collapse ? 4 : 34 + (index - 1) * 23;
    const y = section.y + 2;
    const help = t(collapse ? 'graph.device.toggle' : 'graph.device.' + choice, { name: label });
    const enabled = section.sources.length > 0;
    const selected = enabled && section.sources.every(source => choice === 'pause' ? source.enabled === false
      : source.enabled !== false && !!source.previewOnly === (choice === 'debug'));
    const control = svg('g', {
      class: 'section-control', role: 'button', tabindex: collapse || enabled ? 0 : -1,
      'data-section-control': choice, 'aria-label': collapse ? label : help, 'data-tooltip': help,
      ...(collapse ? { 'aria-expanded': String(!section.closed) } : { 'aria-pressed': selected, 'aria-disabled': !enabled })
    });
    control.append(
      svg('rect', { x, y, width: 22, height: 23 }),
      svg('path', { d: paths[choice], transform: `translate(${x + 2} ${y + 2.5}) scale(.75)${collapse && section.closed ? ' rotate(-90 12 12)' : ''}` })
    );
    const activate = event => {
      event.stopPropagation();
      if (collapse) toggle();
      else if (enabled && !selected) setMode(choice);
    };
    control.addEventListener('click', activate);
    control.addEventListener('keydown', event => {
      if (!['Enter', ' '].includes(event.key)) return;
      event.preventDefault();
      activate(event);
    });
    return control;
  });
}
