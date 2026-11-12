**FREE
// CALL USRMNT: user maintenance (System Options on the main menu, option 1). Lists the users in the table
// QFAKE_USER, where the servers (the 5250 sign-on, the sign-on server, Network Print and the remote
// command server) check sign-ons. A user's default library (DFTLIB) is the one its names are looked
// for in first, and where its new source and SQL tables go; QFAKE_LIBRARY lists the libraries there are.
// F6 creates a user, 2 changes one and 4 deletes one. A change holds at the next sign-on.
// The fake keeps passwords as typed, so that every server can check them: don't use real ones.
dcl-f usrmntd workstn sfile(row:rrn);
dcl-s rrn int(5);

dow not *in03;
  load();
  write keys;
  exfmt list;
  msg = '';
  if *in03 or *in12;
    leave;
  endif;
  if *in06;
    create();
    iter;
  endif;
  readc row;
  dow not %eof;
    if opt = '2';
      change();
    elseif opt = '4';
      remove();
    endif;
    if *in03;
      leave;
    endif;
    readc row;
  enddo;
enddo;
*inlr = *on;
return;

// Clears the subfile and fills it with every user.
dcl-proc load;
  *in40 = *off;
  write list;
  rrn = 0;
  exec sql declare found cursor for
    select username, status, pwdexp, text, dftlib from qfake_user order by username for read only;
  exec sql open found;
  exec sql fetch next from found into :uname, :ustatus, :upwdexp, :utext, :udftlib;
  dow sqlcode = 0;
    rrn += 1;
    opt = ' ';
    udesc = utext;
    write row;
    exec sql fetch next from found into :uname, :ustatus, :upwdexp, :utext, :udftlib;
  enddo;
  exec sql close found;
  *in40 = (rrn > 0);
end-proc;

// Asks for a new user until it's valid, or F12, and adds it.
dcl-proc create;
  uname = ' ';
  upwd = ' ';
  upwd2 = ' ';
  ustatus = '*ENABLED';
  upwdexp = '*NO';
  utext = ' ';
  udftlib = 'QGPL';
  clearerrors();
  dou not invalid(*on);
    exfmt newuser;
    if *in12 or *in03;
      *in12 = *off;
      return;
    endif;
  enddo;
  exec sql insert into qfake_user (username, password, status, pwdexp, text, dftlib)
    values (:uname, :upwd, :ustatus, :upwdexp, :utext, :udftlib);
  if sqlcode = 0;
    msg = 'User ' + %trim(uname) + ' created.';
  else;
    msg = 'Could not create the user: SQLCODE ' + %char(sqlcode);
  endif;
end-proc;

// Shows the user in UNAME for change, until it's valid, or F12. A blank password keeps the old one.
dcl-proc change;
  dcl-s wanted char(10);
  wanted = uname;
  exec sql select status, pwdexp, text, dftlib into :ustatus, :upwdexp, :utext, :udftlib
    from qfake_user where username = :wanted;
  if sqlcode <> 0;
    msg = 'User ' + %trim(wanted) + ' was not found.';
    return;
  endif;
  uname = wanted;
  upwd = ' ';
  upwd2 = ' ';
  clearerrors();
  dou not invalid(*off);
    exfmt chguser;
    if *in12 or *in03;
      *in12 = *off;
      return;
    endif;
  enddo;
  if upwd = *blanks;
    exec sql update qfake_user set status = :ustatus, pwdexp = :upwdexp, text = :utext, dftlib = :udftlib
      where username = :wanted;
  else;
    exec sql update qfake_user set password = :upwd, status = :ustatus, pwdexp = :upwdexp, text = :utext, dftlib = :udftlib
      where username = :wanted;
  endif;
  if sqlcode = 0;
    msg = 'User ' + %trim(wanted) + ' changed.';
  else;
    msg = 'Could not change the user: SQLCODE ' + %char(sqlcode);
  endif;
end-proc;

// Asks to confirm deleting the user in UNAME.
dcl-proc remove;
  dcl-s wanted char(10);
  wanted = uname;
  exec sql select text into :utext from qfake_user where username = :wanted;
  if sqlcode <> 0;
    msg = 'User ' + %trim(wanted) + ' was not found.';
    return;
  endif;
  uname = wanted;
  exfmt confirm;
  if *in12 or *in03;
    *in12 = *off;
    return;
  endif;
  exec sql delete from qfake_user where username = :wanted;
  msg = 'User ' + %trim(wanted) + ' deleted.';
end-proc;

dcl-proc clearerrors;
  *in50 = *off;
  *in51 = *off;
  *in52 = *off;
  *in53 = *off;
  *in54 = *off;
  *in55 = *off;
  *in56 = *off;
end-proc;

// Sets the error indicators (50 name, 51 exists, 52 password needed, 53 passwords differ, 54 status,
// 55 password expired, 56 library) for what was typed. Returns whether any is on.
dcl-proc invalid;
  dcl-pi *n ind;
    creating ind const;
  end-pi;
  dcl-s count int(10);
  dcl-c FIRST 'ABCDEFGHIJKLMNOPQRSTUVWXYZ#$@';
  dcl-c REST 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789#$@_';
  clearerrors();
  ustatus = %upper(ustatus);
  upwdexp = %upper(upwdexp);
  udftlib = %upper(udftlib);
  if udftlib = *blanks;
    udftlib = 'QGPL';
  endif;
  exec sql select count(*) into :count from qfake_library where name = :udftlib;
  *in56 = (count = 0);
  if creating;
    uname = %upper(uname);
    *in50 = (uname = *blanks
      or %check(FIRST:%subst(uname:1:1)) > 0
      or %check(REST:%trim(uname)) > 0);
    if not *in50;
      exec sql select count(*) into :count from qfake_user where username = :uname;
      *in51 = (count > 0);
    endif;
    *in52 = (upwd = *blanks);
  endif;
  *in53 = (upwd <> upwd2);
  *in54 = (ustatus <> '*ENABLED' and ustatus <> '*DISABLED');
  *in55 = (upwdexp <> '*YES' and upwdexp <> '*NO');
  return *in50 or *in51 or *in52 or *in53 or *in54 or *in55 or *in56;
end-proc;
