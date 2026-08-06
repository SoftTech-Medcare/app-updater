<!--
Engineering Notebook
บังคับสำหรับ Feature ที่ใช้เวลาพัฒนามากกว่า 1 วัน หรือมีผลกระทบต่อระบบ
คิดก่อนเขียนโค้ด — ไม่ใช่เอกสารรายงานสถานะ
-->

## Requirement

<!-- ผู้ใช้คือใคร? เจอปัญหาอะไร? Feature นี้ช่วยแก้ปัญหาอะไร? ผลลัพธ์ที่ต้องการคืออะไร? -->

-

## Assumption

<!-- สิ่งที่กำลังสมมติไว้ ให้ทีมตรวจสอบก่อน/ระหว่าง Review -->

-

## Business Flow

<!-- ลำดับการทำงานของ Feature (ไม่จำเป็นต้องใช้ UML) อธิบายได้โดยไม่ต้องเปิด Source Code -->

1.
2.
3.

## API / Database ที่เกี่ยวข้อง

### API

- API ที่เรียกใช้:
- API ใหม่ที่สร้าง:
- API ที่ได้รับผลกระทบ:

### Database

- ตารางที่เกี่ยวข้อง:
- Column ที่เพิ่มหรือแก้ไข:
- Migration:
- Index / Constraint / Transaction:
- ผลกระทบต่อระบบเดิม:

## Edge Case

### Input

-

### User Behavior

-

### Business Rule

-

### System Failure

-

### Concurrency

-

### Scale

-

## Risk

<!-- ความเสี่ยงก่อนเริ่มพัฒนา / สิ่งที่ควรได้รับการช่วยเหลือหรือ Review เพิ่มเติม -->

-

## Alternative Solution

<!-- เสนออย่างน้อย 2 วิธี พร้อมข้อดี ข้อเสีย และเหตุผลที่เลือกแนวทางสุดท้าย -->

### แนวทาง A

- ข้อดี:
- ข้อเสีย:

### แนวทาง B

- ข้อดี:
- ข้อเสีย:

### เหตุผลที่เลือก

-

## Definition of Done

- [ ] Business Logic ถูกต้อง
- [ ] ครอบคลุม Edge Case ที่ทราบ
- [ ] มี Error Handling
- [ ] มี Logging ที่เหมาะสม
- [ ] Performance อยู่ในเกณฑ์
- [ ] Security ถูกต้อง
- [ ] Transaction ถูกต้อง (ถ้ามี)
- [ ] ผ่านการทดสอบ
- [ ] พร้อม Code Review
- [ ] ไม่มี Known Issue ที่ยังไม่ได้รับการยอมรับ
