// hammer_autosave.js: Frida agent for hammer_autosave.py (cubehost.exe only, Steam closed).
//
// hammer.dll (1.41.9.3, 10-29 build) autosave, reached from the hammer_force_autosave command
// (callback 180128660 -> 181497970(this, force = 1)): for each open document (list at [this]+0x60,
// count +0x58) it calls the document's vfunc +0x80 (does it need an autosave) and with force set skips
// that test, but it always skips a document whose byte +0x11 is set; the write itself is the document's
// vfunc +0x90 (document, path, autosave path, 1, force).
//
// The recovery prompt (181497d40, at startup after a killed session): the files of the last session that
// have autosaves come from 181496bd0 into a list at [rsp+0x30] (count, then char* array at +8) and the
// prompt is a QMessageBox question (Yes 0x4000 | No 0x10000) called at 181497e74 through the IAT slot
// 18169bfb0. Yes reloads the autosaves; No only marks those files handled (record +8 = 1, +9 = 0) and
// deletes nothing. When every listed file is in the s2c_rc_probe addon's maps (our probe and decompile
// maps, never the user's own), the agent answers No without showing it; otherwise the dialog shows and the harness stops.
//
// FORCE_ALL (rpc.exports.forceall(true)): clear byte +0x11 just before the test, so every open document
// is written. Each document seen is reported with its vfunc +0x80 answer and its +0x11 byte.
'use strict';

const OFFSETS = {
  needTest: 0x1497a10,   // test al, al after call [rax+0x80]; r15 = document
  skipTest: 0x1497a1c,   // cmp byte ptr [r15+0x11], 0
  write: 0x1497aa3,      // call r10 (document vfunc +0x90)
  recoverList: 0x1497dab, // after 181496bd0 filled [rsp+0x30]
  recoverAsk: 0x1497e7a,  // return address of the question call
  questionSlot: 0x169bfb0,
};
let recoverNames = null;

let forceAll = false;
let hooked = false;

function hook() {
  const m = Process.findModuleByName('hammer.dll');
  if (!m || hooked) return !!m;
  hooked = true;
  Interceptor.attach(m.base.add(OFFSETS.needTest), function () {
    send({ kind: 'need', doc: this.context.r15.toString(), al: this.context.rax.and(0xff).toInt32() });
  });
  Interceptor.attach(m.base.add(OFFSETS.skipTest), function () {
    const doc = this.context.r15;
    const flag = doc.add(0x11).readU8();
    send({ kind: 'skipflag', doc: doc.toString(), flag });
    if (forceAll && flag !== 0) doc.add(0x11).writeU8(0);
  });
  Interceptor.attach(m.base.add(OFFSETS.write), function () {
    const read = (p) => { try { return p.isNull() ? null : p.readUtf8String(); } catch (e) { return null; } };
    send({ kind: 'write', doc: this.context.rcx.toString(), path: read(this.context.rdx), autosave: read(this.context.r8) });
  });
  Interceptor.attach(m.base.add(OFFSETS.recoverList), function () {
    const list = this.context.rsp.add(0x30);
    const n = list.readS32();
    const items = list.add(8).readPointer();
    const strings = (at, count) => {
      const out = [];
      for (let i = 0; i < count; i++) {
        const p = at.add(i * 8).readPointer();
        out.push(p.isNull() ? '' : p.readUtf8String());
      }
      return out;
    };
    // The prompt also shows for the pending list at [this]+0x28 (count +0x20), which No empties (1814980bc).
    const pending = strings(this.context.rbx.add(0x28).readPointer(), this.context.rbx.add(0x20).readS32());
    recoverNames = strings(items, n).concat(pending);
    send({ kind: 'recover-list', names: recoverNames, pending: pending.length });
  });
  const question = m.base.add(OFFSETS.questionSlot).readPointer();
  const original = new NativeFunction(question, 'int', ['pointer', 'pointer', 'pointer', 'int', 'int']);
  const askAt = m.base.add(OFFSETS.recoverAsk);
  Interceptor.replace(question, new NativeCallback(function (parent, title, text, buttons, def) {
    if (this.returnAddress.equals(askAt) && recoverNames !== null && recoverNames.length > 0
        && recoverNames.every(n => /[\\/]csgo_addons[\\/]s2c_rc_probe[\\/]maps[\\/]/i.test(n))) {
      send({ kind: 'recover-answered', answer: 'No', names: recoverNames });
      return 0x10000;
    }
    return original(parent, title, text, buttons, def);
  }, 'int', ['pointer', 'pointer', 'pointer', 'int', 'int']));
  send({ kind: 'hooked', base: m.base.toString() });
  return true;
}

const timer = setInterval(() => { if (hook()) clearInterval(timer); }, 20);

rpc.exports = {
  forceall(on) { forceAll = !!on; return forceAll; },
};
