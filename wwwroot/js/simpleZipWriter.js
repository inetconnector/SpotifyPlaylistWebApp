export class SimpleZipWriter {
  constructor() { this.files = []; this.offset = 0; }
  async add(name, arrayBuffer) {
    const utf8 = new TextEncoder().encode(name);
    const crc = crc32(arrayBuffer);
    const size = arrayBuffer.byteLength >>> 0;
    const mod = dosTime(new Date());
    const localHeader = new Uint8Array(30 + utf8.length);
    writeU32(localHeader,0,0x04034b50); writeU16(localHeader,4,20);
    writeU16(localHeader,6,0); writeU16(localHeader,8,0);
    writeU16(localHeader,10,mod.time); writeU16(localHeader,12,mod.date);
    writeU32(localHeader,14,crc); writeU32(localHeader,18,size); writeU32(localHeader,22,size);
    writeU16(localHeader,26,utf8.length); writeU16(localHeader,28,0);
    localHeader.set(utf8,30);
    this.files.push(localHeader); this.files.push(arrayBuffer);
  }
  async close(){
    // compute central dir
    const central = []; let offset = 0; let count = 0;
    for (let i=0;i<this.files.length;i+=2){
      const header = this.files[i]; const data = this.files[i+1];
      const nameLen = header[26] | (header[27]<<8);
      const name = header.slice(30,30+nameLen);
      const crc = readU32(header,14); const size = readU32(header,18);
      const mod = { time: (header[10] | (header[11]<<8)), date:(header[12] | (header[13]<<8)) };
      const cd = new Uint8Array(46 + nameLen);
      writeU32(cd,0,0x02014b50); writeU16(cd,4,20); writeU16(cd,6,20);
      writeU16(cd,8,0); writeU16(cd,10,0); writeU16(cd,12,mod.time); writeU16(cd,14,mod.date);
      writeU32(cd,16,crc); writeU32(cd,20,size); writeU32(cd,24,size);
      writeU16(cd,28,nameLen); writeU16(cd,30,0); writeU16(cd,32,0); writeU16(cd,34,0); writeU16(cd,36,0);
      writeU32(cd,38,0); writeU32(cd,42,offset);
      cd.set(name,46);
      central.push(cd);
      offset += header.length + data.byteLength;
      count += 1;
    }
    const centralSize = central.reduce((s,u)=>s+u.length,0);
    const centralOffset = offset;
    const end = new Uint8Array(22);
    writeU32(end,0,0x06054b50);
    writeU16(end,4,count); writeU16(end,6,count);
    writeU32(end,8,centralSize); writeU32(end,12,centralOffset); writeU16(end,16,0);
    const parts = [];
    for (let i=0;i<this.files.length;i+=2){ parts.push(this.files[i]); parts.push(new Uint8Array(this.files[i+1])); }
    for (const c of central) parts.push(c);
    parts.push(end);
    return new Blob(parts, { type: "application/zip" });
  }
}
function dosTime(d){ const t=(d.getHours()<<11)|(d.getMinutes()<<5)|Math.floor(d.getSeconds()/2); const da=((d.getFullYear()-1980)<<9)|((d.getMonth()+1)<<5)|d.getDate(); return {time:t,date:da}; }
function writeU16(b,o,v){ b[o]=v&255; b[o+1]=(v>>>8)&255; }
function writeU32(b,o,v){ b[o]=v&255; b[o+1]=(v>>>8)&255; b[o+2]=(v>>>16)&255; b[o+3]=(v>>>24)&255; }
function readU32(b,o){ return b[o]|(b[o+1]<<8)|(b[o+2]<<16)|(b[o+3]<<24); }
function crc32(ab){ const t=table(); const u=new Uint8Array(ab); let c=~0; for(let i=0;i<u.length;i++) c=(c>>>8)^t[(c^u[i])&255]; return ~c>>>0; }
let _t; function table(){ if(_t) return _t; const T=new Uint32Array(256); for(let i=0;i<256;i++){ let c=i; for(let k=0;k<8;k++) c=(c&1)?(0xedb88320^(c>>>1)):(c>>>1); T[i]=c>>>0; } _t=T; return T; }