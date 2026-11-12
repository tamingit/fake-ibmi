**FREE
// CALL CUSTMNT: customer maintenance. 1 lists or searches by name, 2 looks one up by number,
// 3 creates a customer, 4 prints one. The table is CUSTOMERS (see customers.sql).
// Printing writes the record to printer file CUSTPRT, which becomes a spooled file of that name
// when the program ends.
dcl-f custmntd workstn sfile(row:rrn);
dcl-f custprt printer(80);
dcl-s rrn int(5);
dcl-s line char(80);

dow not *in03;
  choice = ' ';
  exfmt menu;
  msg = '';
  if *in03;
    leave;
  endif;
  select;
    when choice = '1';
      list();
    when choice = '2';
      lookup(*off);
    when choice = '3';
      create();
    when choice = '4';
      lookup(*on);
    when choice <> ' ';
      msg = 'Selection must be 1, 2, 3 or 4.';
  endsl;
enddo;
*inlr = *on;
return;

// List, with the search name as a filter. Enter searches again; 5 shows a customer; F12 returns.
dcl-proc list;
  filter = ' ';
  dou *in12 or *in03;
    load();
    write keys;
    exfmt list;
    msg = '';
    if *in12 or *in03;
      leave;
    endif;
    readc row;
    dow not %eof;
      if opt = '5';
        show();
      elseif opt = '6';
        printcust();
      endif;
      readc row;
    enddo;
  enddo;
  *in12 = *off;
end-proc;

// Clears the subfile and fills it with the customers whose name contains the filter.
dcl-proc load;
  dcl-s pattern varchar(22);
  *in40 = *off;
  write list;
  rrn = 0;
  pattern = '%' + %trim(filter) + '%';
  exec sql declare found cursor for
    select id, name, city, balance from demolib/customers
    where upper(name) like upper(:pattern) order by name for read only;
  exec sql open found;
  exec sql fetch next from found into :id, :name, :city, :balance;
  dow sqlcode = 0;
    rrn += 1;
    opt = ' ';
    write row;
    exec sql fetch next from found into :id, :name, :city, :balance;
  enddo;
  exec sql close found;
  *in40 = (rrn > 0);
end-proc;

// Shows the customer with the number in ID.
dcl-proc show;
  dcl-s wanted packed(5:0);
  wanted = id;
  exec sql select name, city, email, phone, balance
    into :name, :city, :email, :phone, :balance
    from demolib/customers where id = :wanted;
  id = wanted;
  exfmt detail;
end-proc;

// Prints the customer with the number in ID: one line at a time to the printer file.
dcl-proc printcust;
  dcl-s wanted packed(5:0);
  wanted = id;
  exec sql select name, city, email, phone, balance
    into :name, :city, :email, :phone, :balance
    from demolib/customers where id = :wanted;
  if sqlcode <> 0;
    msg = 'Customer ' + %char(wanted) + ' was not found.';
    return;
  endif;
  line = 'CUSTOMER RECORD';
  write custprt line;
  line = '---------------';
  write custprt line;
  line = 'Number  : ' + %char(wanted);
  write custprt line;
  line = 'Name    : ' + %trim(name);
  write custprt line;
  line = 'City    : ' + %trim(city);
  write custprt line;
  line = 'Email   : ' + %trim(email);
  write custprt line;
  line = 'Phone   : ' + %trim(phone);
  write custprt line;
  line = 'Balance : ' + %trim(%editc(balance:'J'));
  write custprt line;
  line = ' ';
  write custprt line;
  line = 'Printed ' + %char(%date());
  write custprt line;
  msg = 'Customer ' + %char(wanted) + ' printed to spooled file CUSTPRT.';
end-proc;

// Asks for a customer number until it finds one, or F12. Shows the customer, or prints it.
dcl-proc lookup;
  dcl-pi *n;
    forprint ind const;
  end-pi;
  title = 'Look Up Customer';
  if forprint;
    title = 'Print Customer Record';
  endif;
  *in52 = *off;
  dou *in12;
    lkid = 0;
    exfmt lookup;
    if *in12 or *in03;
      leave;
    endif;
    id = lkid;
    exec sql select count(*) into :rrn from demolib/customers where id = :lkid;
    *in52 = (rrn = 0);
    if not *in52;
      if forprint;
        printcust();
      else;
        show();
      endif;
      leave;
    endif;
  enddo;
  *in12 = *off;
  *in52 = *off;
end-proc;

// Takes a new customer's details and gives it the next number.
dcl-proc create;
  dcl-s newid packed(5:0);
  *in50 = *off;
  name = ' ';
  city = ' ';
  email = ' ';
  phone = ' ';
  dou not *in50;
    exfmt newcust;
    if *in12 or *in03;
      *in12 = *off;
      return;
    endif;
    *in50 = (name = *blanks);
  enddo;
  exec sql select coalesce(max(id), 0) + 1 into :newid from demolib/customers;
  exec sql insert into demolib/customers (id, name, city, email, phone, balance)
    values (:newid, :name, :city, :email, :phone, 0);
  if sqlcode = 0;
    msg = 'Customer ' + %char(newid) + ' created.';
  else;
    msg = 'Could not create the customer: SQLCODE ' + %char(sqlcode);
  endif;
end-proc;
