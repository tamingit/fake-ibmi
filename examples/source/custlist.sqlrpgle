**FREE
// CALL CUSTLIST: the customers in a subfile. Option 5 shows one, and its name can be changed.
ctl-opt dftactgrp(*no);

dcl-f custlistd workstn sfile(row:rrn);
dcl-s rrn int(5);

dow not *in03;
  load();
  write keys;
  exfmt list;
  if *in03;
    leave;
  endif;
  readc row;
  dow not %eof;
    if opt = '5';
      show();
    endif;
    readc row;
  enddo;
enddo;
*inlr = *on;
return;

// Clears the subfile and fills it from the table.
dcl-proc load;
  *in40 = *off;
  write list;
  rrn = 0;
  exec sql declare customers cursor for
    select id, name, balance from demolib/customer order by id for read only;
  exec sql open customers;
  exec sql fetch next from customers into :id, :name, :balance;
  dow sqlcode = 0;
    rrn += 1;
    opt = ' ';
    write row;
    exec sql fetch next from customers into :id, :name, :balance;
  enddo;
  exec sql close customers;
  *in40 = (rrn > 0);
end-proc;

// Shows the customer the subfile record holds, until its name is there or F12 is pressed.
dcl-proc show;
  *in50 = *off;
  dou not *in50;
    exfmt detail;
    if *in12;
      return;
    endif;
    *in50 = (name = *blanks);
  enddo;
  exec sql update demolib/customer set name = :name where id = :id;
end-proc;
