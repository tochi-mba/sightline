package com.rextechnologies.sightline.protocol.gpsock

import com.rextechnologies.sightline.protocol.Golden
import org.xml.sax.SAXException
import org.xml.sax.SAXParseException
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertSame
import kotlin.test.assertTrue

/**
 * The settings catalogue, against the menu the reference camera actually sent.
 *
 * `golden/menu/reference-camera.xml` is 18,523 bytes read off the camera over GPSOCKET on
 * 2026-10-02 (firmware 20240708 V1.3). Parsing the real document is what proves the app will show
 * the settings this camera has, rather than a table someone typed out of a manual.
 */
class MenuCatalogTest {
    private fun referenceText(): String = Golden.text("menu/reference-camera.xml")

    private fun reference(): MenuCatalog = MenuCatalog.parse(referenceText())

    /** A menu with one category whose settings are [settings], written as the camera writes them. */
    private fun menu(settings: String, category: String = "<Name>Record</Name>"): String =
        "<Menu><Categories><Category>$category<Settings>$settings</Settings></Category></Categories></Menu>"

    @Test
    fun `the reference camera menu parses`() {
        val catalogue = reference()

        assertEquals(21, catalogue.settings.size)
    }

    @Test
    fun `it finds the four groups the camera files its settings under`() {
        assertEquals(listOf("Record", "Capture", "System", "Wifi"), reference().categories)
    }

    @Test
    fun `record resolution offers everything up to 4K`() {
        val resolution = assertNotNull(reference().find(0x0000))

        assertEquals("Resolution", resolution.name)
        assertEquals("Record", resolution.category)
        assertEquals(MenuSettingKind.Choice, resolution.kind)
        assertContains(resolution.choices.map { it.label }, "4K")
        assertEquals("4K", resolution.labelFor(0))
    }

    @Test
    fun `capture resolution is a different setting from record resolution`() {
        // Both are called "Resolution"; only the id tells them apart, which is why the app keys
        // everything on the id and never on the name.
        val capture = assertNotNull(reference().find(0x0100))

        assertEquals("Capture", capture.category)
        assertTrue(capture.choices.any { it.label.startsWith("16M") })
    }

    @Test
    fun `an action is not offered as a value to choose`() {
        val format = assertNotNull(reference().find(0x0207))

        assertEquals("Format", format.name)
        assertEquals(MenuSettingKind.Action, format.kind)
        assertTrue(format.choices.isEmpty())
    }

    @Test
    fun `the version the camera reports is read only`() {
        val version = assertNotNull(reference().find(0x0209))

        assertEquals(MenuSettingKind.ReadOnly, version.kind)
        assertFalse(version.isWritable)
    }

    @Test
    fun `the wifi name is text rather than a choice`() {
        val name = assertNotNull(reference().find(0x0300))

        assertEquals(MenuSettingKind.Text, name.kind)
        assertTrue(name.isWritable)
    }

    @Test
    fun `a value the camera never offered is shown as itself rather than guessed at`() {
        assertEquals("99", assertNotNull(reference().find(0x0000)).labelFor(99))
    }

    @Test
    fun `padding after the final tag does not stop it parsing`() {
        // The camera pads the last chunk to its buffer size, so a real document has rubbish on the
        // end. Trimming to the closing tag is what makes it well-formed.
        val padded = referenceText() + String(CharArray(4)) + "rubbish"

        assertEquals(21, MenuCatalog.parse(padded).settings.size)
    }

    @Test
    fun `a document with no closing tag is refused rather than half read`() {
        assertFailsWith<GpSockProtocolException> { MenuCatalog.parse("<Menu><Categories>") }
    }

    @Test
    fun `a menu listing nothing is refused`() {
        assertFailsWith<GpSockProtocolException> { MenuCatalog.parse("<Menu></Menu>") }
    }

    @Test
    fun `a setting with no id is left out rather than given a made up one`() {
        val xml =
            """
            <Menu><Categories><Category><Name>Record</Name><Settings>
              <Setting><Name>Good</Name><ID>0x0001</ID><Type>0x00</Type><Default>0x00</Default></Setting>
              <Setting><Name>Nameless but present</Name><Type>0x00</Type></Setting>
            </Settings></Category></Categories></Menu>
            """.trimIndent()

        val catalogue = MenuCatalog.parse(xml)

        assertEquals(1, catalogue.settings.size)
        assertEquals("Good", catalogue.settings[0].name)
    }

    @Test
    fun `the bytes the camera sent parse as its text does, padding and all`() {
        // NUL padding, as the camera's last chunk carries out to its buffer size.
        val padded = Golden.bytes("menu/reference-camera.xml") + ByteArray(100)

        assertEquals(reference().settings, MenuCatalog.parse(padded).settings)
    }

    @Test
    fun `a choice can be written and an action cannot`() {
        val catalogue = reference()

        assertTrue(assertNotNull(catalogue.find(0x0000)).isWritable)
        assertFalse(assertNotNull(catalogue.find(0x0207)).isWritable)
    }

    @Test
    fun `a default the camera writes as text rather than a number is read as zero`() {
        // The Wi-Fi name's default is the name itself, jrxCamera, which is not a menu value.
        assertEquals(0, assertNotNull(reference().find(0x0300)).default)
    }

    @Test
    fun `a setting with no name is left out rather than shown blank`() {
        val catalogue = MenuCatalog.parse(
            menu(
                "<Setting><ID>0x01</ID></Setting>" +
                    "<Setting><Name>   </Name><ID>0x02</ID></Setting>" +
                    "<Setting><Name>Kept</Name><ID>0x03</ID></Setting>",
            ),
        )

        assertEquals(listOf(3), catalogue.settings.map { it.id })
    }

    @Test
    fun `a setting whose id is not a number is left out`() {
        val catalogue = MenuCatalog.parse(
            menu(
                "<Setting><Name>Hex</Name><ID>0xZZ</ID></Setting>" +
                    "<Setting><Name>Word</Name><ID>two</ID></Setting>" +
                    "<Setting><Name>Kept</Name><ID>0x04</ID></Setting>",
            ),
        )

        assertEquals(listOf("Kept"), catalogue.settings.map { it.name })
    }

    @Test
    fun `numbers may be written plainly as well as in hexadecimal`() {
        val catalogue = MenuCatalog.parse(
            menu(
                "<Setting><Name>Plain</Name><ID>256</ID><Type>2</Type><Default>7</Default></Setting>" +
                    "<Setting><Name>Upper</Name><ID>0X10</ID></Setting>",
            ),
        )

        val plain = assertNotNull(catalogue.find(256))
        assertEquals(MenuSettingKind.Text, plain.kind)
        assertEquals(7, plain.default)
        assertEquals("Upper", assertNotNull(catalogue.find(16)).name)
    }

    @Test
    fun `a setting with no type is a choice and one with no default defaults to zero`() {
        val setting = MenuCatalog.parse(menu("<Setting><Name>Bare</Name><ID>0x05</ID></Setting>")).settings.single()

        assertEquals(MenuSettingKind.Choice, setting.kind)
        assertEquals(0, setting.default)
        assertTrue(setting.choices.isEmpty())
    }

    @Test
    fun `a choice missing its value or its label is left out`() {
        val setting = MenuCatalog.parse(
            menu(
                "<Setting><Name>Size</Name><ID>0x06</ID><Values>" +
                    "<Value><Name>No number</Name></Value>" +
                    "<Value><ID>0x01</ID></Value>" +
                    "<Value><ID>0x02</ID><Name>Kept</Name></Value>" +
                    "</Values></Setting>",
            ),
        ).settings.single()

        assertEquals(listOf(MenuChoice(2, "Kept")), setting.choices)
    }

    @Test
    fun `a category with no name of its own is filed under other`() {
        // The setting's name is a grandchild of the category, and is not taken for the category's.
        val setting = MenuCatalog.parse(menu("<Setting><Name>Lost</Name><ID>0x07</ID></Setting>", category = ""))
            .settings.single()

        assertEquals("Other", setting.category)
        assertEquals("Lost", setting.name)
    }

    @Test
    fun `a kind this library does not know is never offered as writable`() {
        val setting = MenuCatalog.parse(menu("<Setting><Name>Future</Name><ID>0x08</ID><Type>0x07</Type></Setting>"))
            .settings.single()

        assertNull(setting.kind)
        assertFalse(setting.isWritable)
    }

    @Test
    fun `a document that is not well formed xml is refused and says why`() {
        val refused = assertFailsWith<GpSockProtocolException> { MenuCatalog.parse("<Menu><Categories></Menu>") }

        assertEquals("The camera's menu is not well-formed XML.", refused.message)
        assertIs<SAXException>(refused.cause)
    }

    @Test
    fun `a document type declaration is refused before any parser can act on it`() {
        // The shape of an external entity attack: a menu that asks the parser to read a local file
        // into a setting's name, where the app would show it. The camera never sends a document
        // type at all, so refusing every one costs nothing.
        val xml = """<!DOCTYPE Menu [<!ENTITY secret SYSTEM "file:///etc/hosts">]>""" +
            menu("<Setting><Name>&secret;</Name><ID>0x09</ID></Setting>")

        val refused = assertFailsWith<GpSockProtocolException> { MenuCatalog.parse(xml) }

        assertContains(assertNotNull(refused.message), "document type")
    }

    @Test
    fun `the parser's warnings are not failures but its errors are`() {
        val problem = SAXParseException("A problem.", null)

        QuietParserErrors.warning(problem)
        assertSame(problem, assertFailsWith<SAXParseException> { QuietParserErrors.error(problem) })
        assertSame(problem, assertFailsWith<SAXParseException> { QuietParserErrors.fatalError(problem) })
    }

    @Test
    fun `every kind is found again from its number`() {
        for (kind in MenuSettingKind.entries) {
            assertEquals(kind, MenuSettingKind.fromCode(kind.code))
        }
    }
}
