export namespace calendar {
	
	export class Event {
	    uid: string;
	    href: string;
	    etag: string;
	    title: string;
	    start: string;
	    end: string;
	    allDay: boolean;
	
	    static createFrom(source: any = {}) {
	        return new Event(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.uid = source["uid"];
	        this.href = source["href"];
	        this.etag = source["etag"];
	        this.title = source["title"];
	        this.start = source["start"];
	        this.end = source["end"];
	        this.allDay = source["allDay"];
	    }
	}

}

export namespace contacts {
	
	export class Contact {
	    uid: string;
	    href: string;
	    etag: string;
	    name: string;
	    email: string;
	    phone: string;
	    notes: string;
	    birthday: string;
	
	    static createFrom(source: any = {}) {
	        return new Contact(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.uid = source["uid"];
	        this.href = source["href"];
	        this.etag = source["etag"];
	        this.name = source["name"];
	        this.email = source["email"];
	        this.phone = source["phone"];
	        this.notes = source["notes"];
	        this.birthday = source["birthday"];
	    }
	}

}

export namespace mail {
	
	export class Attachment {
	    index: number;
	    filename: string;
	    mimeType: string;
	
	    static createFrom(source: any = {}) {
	        return new Attachment(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.index = source["index"];
	        this.filename = source["filename"];
	        this.mimeType = source["mimeType"];
	    }
	}
	export class AuthResult {
	    spf: string;
	    dkim: string;
	    dmarc: string;
	
	    static createFrom(source: any = {}) {
	        return new AuthResult(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.spf = source["spf"];
	        this.dkim = source["dkim"];
	        this.dmarc = source["dmarc"];
	    }
	}
	export class Detail {
	    uid: number;
	    subject: string;
	    from: string;
	    to: string;
	    cc: string;
	    date: string;
	    text: string;
	    html: string;
	    messageId: string;
	    references: string[];
	    attachments: Attachment[];
	    labels: string[];
	    auth: AuthResult;
	    fromName: string;
	    fromAddr: string;
	
	    static createFrom(source: any = {}) {
	        return new Detail(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.uid = source["uid"];
	        this.subject = source["subject"];
	        this.from = source["from"];
	        this.to = source["to"];
	        this.cc = source["cc"];
	        this.date = source["date"];
	        this.text = source["text"];
	        this.html = source["html"];
	        this.messageId = source["messageId"];
	        this.references = source["references"];
	        this.attachments = this.convertValues(source["attachments"], Attachment);
	        this.labels = source["labels"];
	        this.auth = this.convertValues(source["auth"], AuthResult);
	        this.fromName = source["fromName"];
	        this.fromAddr = source["fromAddr"];
	    }
	
		convertValues(a: any, classs: any, asMap: boolean = false): any {
		    if (!a) {
		        return a;
		    }
		    if (a.slice && a.map) {
		        return (a as any[]).map(elem => this.convertValues(elem, classs));
		    } else if ("object" === typeof a) {
		        if (asMap) {
		            for (const key of Object.keys(a)) {
		                a[key] = new classs(a[key]);
		            }
		            return a;
		        }
		        return new classs(a);
		    }
		    return a;
		}
	}
	export class Folder {
	    name: string;
	    unseen: number;
	    delimiter: string;
	    depth: number;
	    label: string;
	
	    static createFrom(source: any = {}) {
	        return new Folder(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.name = source["name"];
	        this.unseen = source["unseen"];
	        this.delimiter = source["delimiter"];
	        this.depth = source["depth"];
	        this.label = source["label"];
	    }
	}
	export class ProbeResult {
	    imapHost: string;
	    imapPort: number;
	    smtpHost: string;
	    smtpPort: number;
	    source: string;
	
	    static createFrom(source: any = {}) {
	        return new ProbeResult(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.imapHost = source["imapHost"];
	        this.imapPort = source["imapPort"];
	        this.smtpHost = source["smtpHost"];
	        this.smtpPort = source["smtpPort"];
	        this.source = source["source"];
	    }
	}
	export class SendAttachment {
	    filename: string;
	    mimeType: string;
	    data: string;
	
	    static createFrom(source: any = {}) {
	        return new SendAttachment(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.filename = source["filename"];
	        this.mimeType = source["mimeType"];
	        this.data = source["data"];
	    }
	}
	export class SendRequest {
	    accountId: string;
	    from: string;
	    to: string;
	    cc: string;
	    bcc: string;
	    subject: string;
	    text: string;
	    html: string;
	    inReplyTo: string;
	    references: string[];
	    attachments: SendAttachment[];
	    requestDSN: boolean;
	
	    static createFrom(source: any = {}) {
	        return new SendRequest(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.accountId = source["accountId"];
	        this.from = source["from"];
	        this.to = source["to"];
	        this.cc = source["cc"];
	        this.bcc = source["bcc"];
	        this.subject = source["subject"];
	        this.text = source["text"];
	        this.html = source["html"];
	        this.inReplyTo = source["inReplyTo"];
	        this.references = source["references"];
	        this.attachments = this.convertValues(source["attachments"], SendAttachment);
	        this.requestDSN = source["requestDSN"];
	    }
	
		convertValues(a: any, classs: any, asMap: boolean = false): any {
		    if (!a) {
		        return a;
		    }
		    if (a.slice && a.map) {
		        return (a as any[]).map(elem => this.convertValues(elem, classs));
		    } else if ("object" === typeof a) {
		        if (asMap) {
		            for (const key of Object.keys(a)) {
		                a[key] = new classs(a[key]);
		            }
		            return a;
		        }
		        return new classs(a);
		    }
		    return a;
		}
	}
	export class SmartCounts {
	    unread: number;
	    flagged: number;
	    unreadFlagged: number;
	
	    static createFrom(source: any = {}) {
	        return new SmartCounts(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.unread = source["unread"];
	        this.flagged = source["flagged"];
	        this.unreadFlagged = source["unreadFlagged"];
	    }
	}
	export class SmartSummary {
	    uid: number;
	    subject: string;
	    from: string;
	    date: string;
	    seen: boolean;
	    flagged: boolean;
	    answered: boolean;
	    hasAttachments: boolean;
	    labels: string[];
	    category: string;
	    accountId: string;
	    folder: string;
	
	    static createFrom(source: any = {}) {
	        return new SmartSummary(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.uid = source["uid"];
	        this.subject = source["subject"];
	        this.from = source["from"];
	        this.date = source["date"];
	        this.seen = source["seen"];
	        this.flagged = source["flagged"];
	        this.answered = source["answered"];
	        this.hasAttachments = source["hasAttachments"];
	        this.labels = source["labels"];
	        this.category = source["category"];
	        this.accountId = source["accountId"];
	        this.folder = source["folder"];
	    }
	}
	export class Summary {
	    uid: number;
	    subject: string;
	    from: string;
	    date: string;
	    seen: boolean;
	    flagged: boolean;
	    answered: boolean;
	    hasAttachments: boolean;
	    labels: string[];
	    category: string;
	
	    static createFrom(source: any = {}) {
	        return new Summary(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.uid = source["uid"];
	        this.subject = source["subject"];
	        this.from = source["from"];
	        this.date = source["date"];
	        this.seen = source["seen"];
	        this.flagged = source["flagged"];
	        this.answered = source["answered"];
	        this.hasAttachments = source["hasAttachments"];
	        this.labels = source["labels"];
	        this.category = source["category"];
	    }
	}
	export class UnifiedSummary {
	    uid: number;
	    subject: string;
	    from: string;
	    date: string;
	    seen: boolean;
	    flagged: boolean;
	    answered: boolean;
	    hasAttachments: boolean;
	    labels: string[];
	    category: string;
	    accountId: string;
	    accountEmail: string;
	
	    static createFrom(source: any = {}) {
	        return new UnifiedSummary(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.uid = source["uid"];
	        this.subject = source["subject"];
	        this.from = source["from"];
	        this.date = source["date"];
	        this.seen = source["seen"];
	        this.flagged = source["flagged"];
	        this.answered = source["answered"];
	        this.hasAttachments = source["hasAttachments"];
	        this.labels = source["labels"];
	        this.category = source["category"];
	        this.accountId = source["accountId"];
	        this.accountEmail = source["accountEmail"];
	    }
	}

}

export namespace mailcow {
	
	export class Alias {
	    id: any;
	    address: string;
	    goto: string;
	    active: any;
	
	    static createFrom(source: any = {}) {
	        return new Alias(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.id = source["id"];
	        this.address = source["address"];
	        this.goto = source["goto"];
	        this.active = source["active"];
	    }
	}
	export class AppPassword {
	    id: any;
	    name: string;
	    active: any;
	
	    static createFrom(source: any = {}) {
	        return new AppPassword(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.id = source["id"];
	        this.name = source["name"];
	        this.active = source["active"];
	    }
	}
	export class QItem {
	    id: any;
	    subject: string;
	    sender: string;
	    rcpt: string;
	    score: any;
	    created: any;
	
	    static createFrom(source: any = {}) {
	        return new QItem(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.id = source["id"];
	        this.subject = source["subject"];
	        this.sender = source["sender"];
	        this.rcpt = source["rcpt"];
	        this.score = source["score"];
	        this.created = source["created"];
	    }
	}
	export class Quota {
	    bytes: number;
	    used: number;
	    messages: number;
	
	    static createFrom(source: any = {}) {
	        return new Quota(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.bytes = source["bytes"];
	        this.used = source["used"];
	        this.messages = source["messages"];
	    }
	}

}

export namespace main {
	
	export class ArchivedFile {
	    name: string;
	    path: string;
	    size: number;
	    date: string;
	
	    static createFrom(source: any = {}) {
	        return new ArchivedFile(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.name = source["name"];
	        this.path = source["path"];
	        this.size = source["size"];
	        this.date = source["date"];
	    }
	}
	export class PGPKeyInfo {
	    fingerprint: string;
	    name: string;
	    email: string;
	    isPrivate: boolean;
	
	    static createFrom(source: any = {}) {
	        return new PGPKeyInfo(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.fingerprint = source["fingerprint"];
	        this.name = source["name"];
	        this.email = source["email"];
	        this.isPrivate = source["isPrivate"];
	    }
	}
	export class ScheduledView {
	    id: string;
	    to: string;
	    subject: string;
	    sendAt: string;
	
	    static createFrom(source: any = {}) {
	        return new ScheduledView(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.id = source["id"];
	        this.to = source["to"];
	        this.subject = source["subject"];
	        this.sendAt = source["sendAt"];
	    }
	}

}

export namespace sieve {
	
	export class Script {
	    name: string;
	    active: boolean;
	
	    static createFrom(source: any = {}) {
	        return new Script(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.name = source["name"];
	        this.active = source["active"];
	    }
	}

}

export namespace store {
	
	export class Account {
	    id: string;
	    name: string;
	    email: string;
	    user: string;
	    password: string;
	    imapHost: string;
	    imapPort: number;
	    smtpHost: string;
	    smtpPort: number;
	    signature: string;
	    color: string;
	    sieveHost: string;
	    sievePort: number;
	    cardDavUrl: string;
	    calDavUrl: string;
	    webDavUrl: string;
	    archiveDir: string;
	    mailcowHost: string;
	    mailcowKey: string;
	
	    static createFrom(source: any = {}) {
	        return new Account(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.id = source["id"];
	        this.name = source["name"];
	        this.email = source["email"];
	        this.user = source["user"];
	        this.password = source["password"];
	        this.imapHost = source["imapHost"];
	        this.imapPort = source["imapPort"];
	        this.smtpHost = source["smtpHost"];
	        this.smtpPort = source["smtpPort"];
	        this.signature = source["signature"];
	        this.color = source["color"];
	        this.sieveHost = source["sieveHost"];
	        this.sievePort = source["sievePort"];
	        this.cardDavUrl = source["cardDavUrl"];
	        this.calDavUrl = source["calDavUrl"];
	        this.webDavUrl = source["webDavUrl"];
	        this.archiveDir = source["archiveDir"];
	        this.mailcowHost = source["mailcowHost"];
	        this.mailcowKey = source["mailcowKey"];
	    }
	}

}

