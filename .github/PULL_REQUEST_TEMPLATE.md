<!--
App Updater — PR checklist
บังคับเมื่องานกระทบ client updater (download / install / verify) ที่ HemoBox / HemoCheckIn ใช้
โครงสร้างย่อ — เน้น client behavior และ rollout
-->

**Summary:** <สรุปการเปลี่ยนแปลงแบบ 1-3 บรรทัด>  
**Risk:** <Low | Medium | High | Critical>  
**Production Impact:** <None | Low | Medium | High>

## 1. Requirement

- ปัญหา / เป้าหมาย:
- Acceptance criteria:
- นอกขอบเขต:

## 2. ประเภทการเปลี่ยนแปลง

- [ ] Download / install flow
- [ ] Version compare / semver
- [ ] Integrity verify (hash / signature)
- [ ] UI / UX บน device
- [ ] Bug fix
- [ ] เอกสาร / อื่น ๆ

## 3. ผลกระทบต่อ client

- แอปที่กระทบ (HemoBox / HemoCheckIn / อื่น ๆ):
- พฤติกรรมที่เปลี่ยนหลังอัปเดต:
- Breaking change: ใช่ / ไม่ใช่

## 4. Edge Cases / Failure Modes

- network drop ระหว่าง download:
- disk เต็ม / permission:
- retry / resume:
- version mismatch หลัง install:

## 5. Risk

- ทำไมถึงระดับความเสี่ยงนี้?
- อะไรที่อาจทำให้ device brick / อัปเดตค้าง / ติดเวอร์ชันผิด?

## 6. Tests & การตรวจ

- [ ] manual / device test (ระบุรุ่น OS / app)
- [ ] regression กับ update server
- หลักฐาน / อ้างอิง:

## 7. Production Impact

- None / Low / Medium / High
- Rollback / containment:

## 8. AI Generated?

- Yes / No
- ใช้ AI สำหรับ: <code / test / docs / analysis / other>

## 9. Sign-off

- Reviewer:
- Approved / Changes requested:
