<!--
PR Template สำหรับ App Updater
ใช้สำหรับแก้ updater client / release — ไม่ใช่ Engineering Notebook ของ Feature
-->

## สรุป

<!-- อธิบายสั้น ๆ ว่าเปลี่ยนอะไร และทำไม -->

-

## ประเภทการเปลี่ยนแปลง

- [ ] Bug fix
- [ ] พฤติกรรม update / download / install
- [ ] UI (Avalonia)
- [ ] Compatibility กับ Update Server
- [ ] Build / CI / packaging
- [ ] เอกสาร / อื่น ๆ

## Version bump ที่ต้องการ

<!-- Release Drafter ใช้ label บน PR -->

- [ ] `major` — breaking
- [ ] `minor` / `enhancement` / `feature`
- [ ] `patch` / `fix` / `bug`
- [ ] ไม่ต้องการ release จาก PR นี้

## ผลกระทบ

- Platform ที่กระทบ (`Windows` / `Mac` / `Linux` / `Linux-Arm`):
- ทำงานร่วมกับ Update Server เวอร์ชัน / contract เดิมได้หรือไม่:
- Breaking change หรือไม่: ใช่ / ไม่ใช่

## Checklist

- [ ] ติด label สำหรับ version bump แล้ว (ถ้าต้อง release)
- [ ] พฤติกรรม check / download / apply update ยังถูกต้อง
- [ ] รองรับ tarball จาก Update Server ตามเดิม
- [ ] ทดสอบบน platform ที่เกี่ยวข้องแล้ว (อย่างน้อย platform หลักที่กระทบ)
- [ ] Build / packaging ไม่พัง (`tar.gz`, version ใน csproj / `version.json` ถ้าแตะ)
- [ ] เอกสาร / README อัปเดตถ้าจำเป็น
- [ ] เข้าใจผลกระทบหลัง merge → tag → CircleCI → Argo → NFS

## วิธีทดสอบ

1.
2.
3.

## ความเสี่ยง / Rollback

-

## หมายเหตุสำหรับ Reviewer

-
