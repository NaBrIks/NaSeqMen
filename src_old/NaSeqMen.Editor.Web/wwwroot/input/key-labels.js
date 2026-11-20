// SPDX-License-Identifier: MIT
const namedKeys = new Map([
  ['2A', 'LShift'], ['36', 'RShift'], ['E0:36', 'RShift'], ['1D', 'LCtrl'], ['E0:1D', 'RCtrl'],
  ['38', 'LAlt'], ['E0:38', 'RAlt'], ['E0:5B', 'LWin'], ['E0:5C', 'RWin'],
  ['01', 'Esc'], ['0E', 'Backspace'], ['0F', 'Tab'], ['1C', 'Enter'], ['E0:1C', 'Num Enter'],
  ['39', 'Space'], ['3A', 'Caps Lock'], ['E0:52', 'Insert'], ['E0:53', 'Delete'],
  ['E0:47', 'Home'], ['E0:4F', 'End'], ['E0:49', 'Page Up'], ['E0:51', 'Page Down'],
  ['E0:48', '↑'], ['E0:50', '↓'], ['E0:4B', '←'], ['E0:4D', '→'], ['57', 'F11'], ['58', 'F12']
]);
for (const [first, keys] of [[0x02, '1234567890'], [0x10, 'QWERTYUIOP'], [0x1e, 'ASDFGHJKL'], [0x2c, 'ZXCVBNM']]) {
  [...keys].forEach((key, index) => namedKeys.set((first + index).toString(16).toUpperCase().padStart(2, '0'), key));
}
for (let index = 0; index < 10; index++) namedKeys.set((0x3b + index).toString(16).toUpperCase(), 'F' + (index + 1));

export function keyName(control) { return control.startsWith('key:') ? namedKeys.get(control.slice(4).toUpperCase()) : undefined; }
export const knownKeyControls = () => [...namedKeys].filter(([code]) => code !== 'E0:36')
  .map(([code, label]) => ['key:' + code, label + ' · ' + code]);
const sides = ['LShift', 'RShift', 'LCtrl', 'RCtrl', 'LAlt', 'RAlt', 'LWin', 'RWin'];
export const isModifierKey = control => sides.includes(keyName(control));
export const heldModifierNames = mask => sides.filter((_, index) => (mask & (1 << index)) !== 0);
const groups = [['Ctrl', 0x0c], ['Alt', 0x30], ['Shift', 0x03], ['Win', 0xc0]];
export const modifierGroups = mask => groups.filter(([, bits]) => (mask & bits) !== 0).map(([name]) => name);
