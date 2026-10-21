# Hardware notes

What has been seen on real printers, as opposed to read in Brother's references. Dates are when it
was observed.

## QL-810W (firmware 1.22)

- **Command Mode must be Raster.** In P-touch Template mode a raster job prints nothing. New units ship
  in Template mode. Some units lock their settings behind the admin password on the printer's label.
- **Port 9100 never answers `ESC i S`.** Status comes from SNMPv1 (`public`) or the web page
  `/general/status.html`.
- **Brother status block over SNMP** (2026-10-21): `1.3.6.1.4.1.2435.3.3.9.1.6.1.0` returns the 32-byte
  block the reference describes under `ESC i S`. Series 0x34, model 0x39, width 62, media type 0x0A
  (the reference's table says 0x4A).
  - Byte 25 (reserved in the reference) reads 0x01 with a DK-22205-sized roll and 0x81 with the DK-22251
    black/red roll. Byte 14 (fixed 0x3F in the reference) reads 0x15 and 0x23. This may identify the roll,
    but it is not used yet.
- **Delivery acknowledgement:** the printer closes the TCP connection once it has read the whole job. If
  the sender tears the connection down early, the printer keeps a partial job and stays BUSY until it is
  switched off and on.
- **Faults hold jobs:** with the cover open or no media, a job is accepted, held, and printed once the
  fault is cleared. Never resend an accepted job.
- **No media while idle:** the display still says READY and no error bit is set; only prtInputStatus
  (critical alert bit) shows it.
- **DK-22251 black/red:** a one-color job prints nothing and leaves the printer in ERROR with the red
  light blinking. Opening the cover does not clear it; switching off and on does, and the job is lost.
  The two-color job prints. The MIB media name is "62mm / 2.4\"" for both rolls.
- **Auto power-off** (if enabled) after 60 idle minutes; a cold boot to ready takes about 77 s.

## PT-P750W (firmware 1.18, network firmware 1.00)

- Raster emulation (2026-10-21). The cassette is 12 mm laminated TZe, white tape with black text (its
  label reads "12mm LAMINATED TAPE WHITE").
- **Printed** (2026-10-21): an orientation pattern and a two-line dated sticker on 12 mm. Both read
  correctly (not mirrored, not upside down), cut cleanly, and the text sat centred across the tape. So
  bitmap row 0 is the top edge of the tape, and the encoder's pin mapping (left margin + row) is right.
- **The label's start (bitmap column 0, the left end as read) leaves the printer first** (2026-10-21).
- **The leading end gets extra blank tape.** The job asks for 14 dots (2 mm) at each end. The trailing
  end came out about as wide as the tape's top and bottom edges. The leading end was about twice that.
  This fits the reference's note that the cutter sits ahead of the head, so a P-touch cannot feed less
  than 24.5 mm. The encoder can't remove that space. `Chain = true` leaves the last label in the
  printer so the next job starts from it, which saves tape across a batch.
- **Waking up:** after an idle power-off, the first SNMP read and the first job got no answer; a job sent
  a few seconds later printed.
- **Port 9100 sends no status back**, with or without a half-close. The reference says the PT-E550W and
  PT-P750W do not support `ESC i S`.
- **SNMP answers like the QL:** hrDeviceStatus 2, hrPrinterStatus 3, display "READY", cover 4 (closed),
  media name "12mm(0.47\")". sysDescr: "Brother NC-22024w".
- **Status block over SNMP:** the same OID as the QL. Series 0x30, model 0x68, width 12, media type 0x01
  (laminated). Width and type are right.
- **The color bytes are not reliable.** With the white cassette, tape color reads 0x06, which the
  reference's table calls yellow. Text color reads 0x08 (black), which matches. Until more cassettes have
  been read, treat both as raw values: `DescribeMedia()` leaves them out.
- **Two connections at once:** the second is accepted at once, not refused. Whether the printer reads two
  jobs in parallel is untested; send one at a time (`SerializedTransport`).
- **Delivery acknowledgement:** after a half-close the printer closes its side within milliseconds, so
  `RawTcpTransport` works unchanged.
- The printer went to sleep (no ping, no SNMP) within about an hour of idling on AC.

- **Cover open** (2026-10-21): the display says "COVER OPEN". The status block sets cover open (error 2,
  0x10) and status type "error occurred", and reports no media, because the cassette can't be read with
  the cover open. The MIB contradicts itself: prtCoverStatus says closed (4) while the door bit (0x08)
  is set.
  - **A job sent with the cover open is held.** The printer took the whole job and closed the connection
    within half a second. The display changed to "ERROR". prtCoverStatus then read open (3), the door
    bit cleared, and the block's phase went to printing. When the cover was closed, the label printed
    normally and the status went back to ready. So never resend a job the printer accepted, exactly as on
    the QL.

Still to check on the PT-P750W:
- what happens to a job sent with no cassette
- what the color bytes read with other cassettes
